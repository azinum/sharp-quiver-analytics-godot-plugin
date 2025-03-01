// Analytics.cs
// Quiver Analytics ported to C#

// Handles sending events to Quiver Analytics (https://quiver.dev/analytics/).
//
// This class manages a request queue, which the plugin user can populate with events.
// Events are sent to the Quiver server one at a time.
// This class manages spacing out requests so as to not overload the server
// and to prevent performance issues in the game.
// If events are not able to be sent due to network connection issues,
// the events are saved to disk when the game exits.
//
// This implementation favors performance over accuracy, so events may be dropped if
// they could lead to performance issues.

using Godot;
using System;
using Godot.Collections;

public partial class Analytics : Node {
	public static Analytics Instance;

	// Note that use_threads has to be turned off on this node because otherwise we get frame rate hitches
	// when the request is slow due to server issues.
	// Not sure why yet, but might be related to https://github.com/godotengine/godot/issues/33479.
	[Export]
	public HttpRequest httpRequest;
	[Export]
	public Timer retryTimer;
	[Export]
	public Timer quitEventTimer;

	// Use this to pick a random player identifier
	const long MAX_INT = 9223372036854775807; // NOTE: unused

	// The maximum rate we can add events to the queue.
	// If this limit is exceeded, requests will be dropped.
	const int MAX_ADD_TO_EVENT_QUEUE_RATE = 50;

	// This controls the maximum size of the request queue that is saved to disk
	// in the situation the events weren't able to be successfully sent.
	// In pathological cases, we may drop events if the queue grows too long.
	const int MAX_QUEUE_SIZE_TO_SAVE_TO_DISK = 200;

	// The file to store queue events that weren't able to be sent due to network or server issues
	const string QUEUE_FILE_NAME = "user://analytics_queue";

	// The server host
	const string SERVER_PATH = "https://quiver.dev";

	// The URL for adding events
	const string ADD_EVENT_PATH = "/analytics/events/add/";

	// Event names can't exceed this length
	const int MAX_EVENT_NAME_LENGTH = 50;

	// The next two parameters guide how often we send artifical quit events.
	// We send these fake quit events because on certain platfomrms (mobile and web),
	// it can be hard to determine when a player ended the game (e.g. they background the app or close a tab).
	// So we just send periodic quit events with session IDs, which are reconciled by the server.
	// 
	// We send a quit event this many seconds after launching the game.
	// We set this fairly low to handle immediate bounces from the game.
	const int INITIAL_QUIT_EVENT_INTERVAL_SECONDS = 10;

	// This is the max interval between sending quit events
	const int MAX_QUIT_EVENT_INTERVAL_SECONDS = 60;

	// Emitted when the sending the final events have been completed
	[Signal]
	public delegate void ExitHandledEventHandler();

	string authToken = (string)ProjectSettings.GetSetting("quiver/general/auth_token", "");
	string configFilePath = (string)ProjectSettings.GetSetting("quiver/analytics/config_file_path", "user://analytics.cfg");
	bool consentRequired = (bool)ProjectSettings.GetSetting("quiver/analytics/player_consent_required", false);
	bool consentRequested = false;
	bool consentGranted = false;
	// TODO: preload
	PackedScene consentDialogScene = (PackedScene)GD.Load<PackedScene>("res://addons/sharp_quiver_analytics/consent_dialog.tscn");
	bool consentDialogShowing = false;
	bool dataCollectionEnabled = false;
	ConfigFile config = new ConfigFile();
	ulong playerId = 0;
	ulong timeSinceFirstRequestInBatch = Time.GetTicksMsec();
	int requestsInBatchCount = 0;
	bool requestInFlight = false;
	Array<Dictionary<string, Variant>> requestQueue = new Array<Dictionary<string, Variant>>();
	bool shouldDrainRequestQueue = false;
	float minRetryTimeSeconds = 2.0f;
	float currentRetryTimeSeconds = 2.0f;
	float maxRetryTimeSeconds = 120.0f;
	bool autoAddEventOnLaunch = (bool)ProjectSettings.GetSetting("quiver/analytics/auto_add_event_on_launch", true);
	bool autoAddEventOnQuit = (bool)ProjectSettings.GetSetting("quiver/analytics/auto_add_event_on_quit", true);
	float quitEventIntervalSeconds = INITIAL_QUIT_EVENT_INTERVAL_SECONDS;
	ulong sessionId = (ulong)(GD.Randi() << 32 | GD.Randi());

	public override void _Ready() {
		Instance = this;
		var err = config.Load(configFilePath);
		if (err == Error.Ok) {
			playerId = (ulong)config.GetValue("general", "player_id");
			consentGranted = (bool)config.GetValue("general", "granted");
			// We use the hash as a basic (but easily bypassable) protection to reduce
			// the chance that the player ID has been tampered with.
			var hash = playerId.ToString().Sha256Text();
			if (hash != (string)config.GetValue("general", "hash")) {
				DirAccess.RemoveAbsolute(configFilePath);
				InitConfig();
			}
		}
		else {
			InitConfig();
		}

		// Check to see if data collection is possible
		if (authToken.Length > 0 && (!consentRequired || consentGranted)) {
			dataCollectionEnabled = true;
		}
		// Let's load any saved events from previous sessions
		// and start processing them, if available.
		LoadQueueFromDisk();
		if (requestQueue.Count != 0) {
			DirAccess.RemoveAbsolute(QUEUE_FILE_NAME);
			ProcessRequests();
		}
		if (autoAddEventOnLaunch) {
			AddEvent("Launched game");
		}
		if (autoAddEventOnQuit) {
			quitEventTimer.Start(quitEventIntervalSeconds);
		}
	}

	// Whether we should be obligated to show the consent dialog to the player
	public static bool ShouldShowConsentDialog() {
		return Instance.consentRequired &&
			!Instance.consentRequested &&
			!Instance.consentGranted; // don't ask again if user has already given consent
	}

	// Show the consent dialog to the user, using the passed in node as the parent
	public static void ShowConsentDialog(Node parent) {
		if (!Instance.consentDialogShowing) {
			Instance.consentDialogShowing = true;
			ConsentDialog consentDialog = (ConsentDialog)Instance.consentDialogScene.Instantiate();
			parent.AddChild(consentDialog);
			consentDialog.ShowWithAnimation();
		}
	}

	// Call this when consent has been granted.
	// The ConsentDialog scene will manage this automatically.
	public static void ApproveDataCollection() {
		Instance.consentRequested = true;
		Instance.consentGranted = true;
		Instance.config.SetValue("general", "requested", Instance.consentRequested);
		Instance.config.SetValue("general", "granted", Instance.consentGranted);
		Instance.config.Save(Instance.configFilePath);
	}

	// Call this when consent has been denied.
	// The ConsentDialog scene will manage this automatically.
	public static void DenyDataCollection() {
		if (Instance.consentGranted) {
			Instance.consentRequested = true;
			Instance.consentGranted = false;
			Instance.config.SetValue("general", "requested", Instance.consentRequested);
			Instance.config.SetValue("general", "granted", Instance.consentGranted);
			Instance.config.Save(Instance.configFilePath);
		}
	}

	// Use this track an event. The name must be 50 characters or less.
	// You can pass in an arbitrary dictionary of properties.
	public static void AddEvent(String name, Dictionary<string, Variant> properties = null) {
		if (!Instance.dataCollectionEnabled) {
			Instance.ProcessRequests();
			return;
		}
		if (name.Length > MAX_EVENT_NAME_LENGTH) {
			GD.PrintErr($"[Quiver Analytics] Event name '{name}' is too long. Must be {MAX_EVENT_NAME_LENGTH} characters or less.");
			Instance.ProcessRequests();
			return;
		}

		// We limit big bursts of event tracking to reduce overusage due to buggy code
		// and to prevent overloading the server.
		var currentTimeMsec = Time.GetTicksMsec();
		if ((currentTimeMsec - Instance.timeSinceFirstRequestInBatch) > 60 * 1000) {
			Instance.timeSinceFirstRequestInBatch = currentTimeMsec;
			Instance.requestsInBatchCount = 0;
		}
		else {
			Instance.requestsInBatchCount += 1;
		}
		if (Instance.requestsInBatchCount > MAX_ADD_TO_EVENT_QUEUE_RATE) {
			GD.PrintErr("[Quiver Analytics] Event tracking was disabled temporarily because max event rate was exceeded.");
			return;
		}

		if (properties == null) {
			properties = new Dictionary<string, Variant>();
		}

		// Auto-add default properties
		properties["$platform"] = OS.GetName();
		properties["$session_id"] = Instance.sessionId;
		properties["$debug"] = OS.IsDebugBuild();
		properties["$export_template"] = OS.HasFeature("template");

		// Add the request to the queue and process the queue
		var request = new Dictionary<string, Variant>() {
			{"url", SERVER_PATH + ADD_EVENT_PATH},
			{"headers", new Array<Variant> { "Authorization: Token " + Instance.authToken }},
			{
				"body", new Dictionary<string, Variant>() { {"name", name}, {"player_id", Instance.playerId}, {"properties", properties}, {"timestamp", Time.GetUnixTimeFromSystem()} }
			}
		};
		Instance.requestQueue.Add(request);
		Instance.ProcessRequests();
	}

	// Ideally, this should be called when a user exits the game,
	// although it may be difficult on certain plaforms.
	// This handles draining the request queue and saving the queue to disk, if necessary.
	public static void HandleExit() {
		Instance.quitEventTimer.Stop();
		Instance.shouldDrainRequestQueue = true;
		if (Instance.autoAddEventOnQuit) {
			AddEvent("Quit game");
		}
		else {
			Instance.ProcessRequests();
		}
		// EmitSignal(SignalName.ExitHandled);
	}

	void SaveQueueToDisk() {
		var f = FileAccess.Open(QUEUE_FILE_NAME, FileAccess.ModeFlags.Write);
		if (f != null) {
			// If the queue is too big, we trim the queue,
			// favoring more recent events (i.e. the back of the queue).
			if (requestQueue.Count > MAX_QUEUE_SIZE_TO_SAVE_TO_DISK) {
				requestQueue = requestQueue.Slice(requestQueue.Count - MAX_QUEUE_SIZE_TO_SAVE_TO_DISK);
				GD.PrintErr("[Quiver Analytics] Request queue overloaded. Events were dropped.");
			}
			f.StoreVar(requestQueue, false);
		}
	}

	void LoadQueueFromDisk() {
		var f = FileAccess.Open(QUEUE_FILE_NAME, FileAccess.ModeFlags.Read);
		if (f != null) {
			requestQueue = (Array<Dictionary<string, Variant>>)f.GetVar();
		}
	}

	void HandleRequestFailure(int responseCode) {
		requestInFlight = false;
		// Drop invalid 4xx events
		// 5xx and transient errors will be presumed to be fixed server-side.
		if (responseCode >= 400 && responseCode <= 499) {
			if (requestQueue.Count > 0) {
				requestQueue.RemoveAt(0); // pop_front()
			}
			GD.PrintErr($"[Quiver Analytics] Event was dropped because it couldn't be processed by the server. Response code {responseCode}.");
		}
		// If we are not in draining mode, we retry with exponential backoff
		if (!shouldDrainRequestQueue) {
			retryTimer.Start(currentRetryTimeSeconds);
			currentRetryTimeSeconds += Mathf.Min(currentRetryTimeSeconds * 2, maxRetryTimeSeconds);
		}
		else {
			// If we are in draining mode, we immediately save the existing queue to disk
			// and use _process_requests() to emit the exit_handled signal.
			// We do this because we want to hurry up and let the player quit the game.
			SaveQueueToDisk();
			requestQueue.Clear();
			ProcessRequests();
		}
	}

	async void ProcessRequests() {
		if (requestQueue.Count > 0 && !requestInFlight) {
			var request = requestQueue[0]; // front()
			requestInFlight = true;
			var error = httpRequest.Request(
				(string)request["url"],
				(string[])request["headers"],
				HttpClient.Method.Post,
				Json.Stringify(request["body"])
			);
			if (error != Error.Ok) {
				HandleRequestFailure((int)error); // TODO(lucas): check if this actually works
			}
		}
		// If we have successfully drained the queue, emit the exit_handled signal
		if (shouldDrainRequestQueue && requestQueue.Count == 0) {
			await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
			EmitSignal(SignalName.ExitHandled);
		}
	}

	void InitConfig() {
		// This should give us a nice randomized player ID with low chance of collision
		playerId = (ulong)(GD.Randi() << 32 | GD.Randi());
		config.SetValue("general", "player_id", playerId);
		// We calculate the hash to prevent the player from arbitrarily changing the player ID
		// in the file. This is easy to bypass, and players could always manually send events
		// anyways, but this provides some basic protection.
		var hash = playerId.ToString().Sha256Text();
		config.SetValue("general", "hash", hash);
		config.SetValue("general", "requested", consentRequested);
		config.SetValue("general", "granted", consentGranted);
		config.Save(configFilePath);

	}

	public void _OnHttpRequestRequestCompleted(int result, int responseCode, String[] headers, byte[] body) {
		if (responseCode >= 200 && responseCode <= 299) {
			requestInFlight = false;
			if (requestQueue.Count > 0) {
				requestQueue.RemoveAt(0);
			}
			currentRetryTimeSeconds = minRetryTimeSeconds;
			// If we are draining the queue, process events as fast as possible
			if (shouldDrainRequestQueue) {
				ProcessRequests();
			}
			// Otherwise, take our time so as not to impact the frame rate
			else {
				retryTimer.Start(currentRetryTimeSeconds);
			}
		}
		else {
			HandleRequestFailure(responseCode);
		}

	}

	public void _OnRetryTimerTimeout() {
		ProcessRequests();
	}

	public void _OnQuitEventTimerTimeout() {
		AddEvent("Quit game");
		quitEventIntervalSeconds = Mathf.Min(quitEventIntervalSeconds + 10, MAX_QUIT_EVENT_INTERVAL_SECONDS);
		quitEventTimer.Start(quitEventIntervalSeconds);
	}
}
