// Plugin.cs

#if TOOLS

using Godot;
using System;
using Godot.Collections;

[Tool]
public partial class Plugin : EditorPlugin {
	string AUTOLOAD_NAME = "Analytics";

	Array<Dictionary<string, Variant>> CUSTOM_PROPERTIES = new Array<Dictionary<string, Variant>> {
		new Dictionary<string, Variant>() { {"name", "quiver/general/auth_token"},                 {"default", ""},                     {"basic", true},  {"general", true}  },
		new Dictionary<string, Variant>() { {"name", "quiver/analytics/player_consent_required"},  {"default", false},                  {"basic", true},  {"general", false} },
		new Dictionary<string, Variant>() { {"name", "quiver/analytics/config_file_path"},         {"default", "user://analytics.cfg"}, {"basic", false}, {"general", false} },
		new Dictionary<string, Variant>() { {"name", "quiver/analytics/auto_add_event_on_launch"}, {"default", true},                   {"basic", false}, {"general", false} },
		new Dictionary<string, Variant>() { {"name", "quiver/analytics/auto_add_event_on_quit"},   {"default", true},                   {"basic", false}, {"general", false} },
	};

	public override void _EnterTree() {
		// Migrate legacy setting
		if (ProjectSettings.HasSetting("quiver/analytics/auth_token")) {
			string authToken = (string)ProjectSettings.GetSetting("quiver/general/auth_token");
			if (!ProjectSettings.HasSetting("quiver/general/auto_token")) {
				ProjectSettings.SetSetting("quiver/general/auth_token", authToken);
			
			}
			ProjectSettings.SetSetting("quiver/analytics/auth_token", Variant.CreateFrom<string>(null));
		}
		foreach (var property in CUSTOM_PROPERTIES) {
			string name = (string)property["name"];
			var defaultValue = property["default"];
			bool basic = (bool)property["basic"];
			if (!ProjectSettings.HasSetting((string)name)) {
				ProjectSettings.SetSetting(name, defaultValue);
				ProjectSettings.SetInitialValue(name, defaultValue);
				if (basic) {
					ProjectSettings.SetAsBasic(name, true);
				}
			}
		}
		AddAutoloadSingleton(AUTOLOAD_NAME, "res://addons/sharp_quiver_analytics/analytics.tscn");
		{
			string authToken = (string)ProjectSettings.GetSetting("quiver/general/auth_token");
			if (string.IsNullOrEmpty(authToken)) {
				GD.PrintErr("[Quiver Analytics] Auth key hasn't been set for Quiver services.");
			}
		}
	}

	public override void _ExitTree() {
		RemoveAutoloadSingleton(AUTOLOAD_NAME);
		foreach (var property in CUSTOM_PROPERTIES) {
			string name = (string)property["name"];
			bool general = (bool)property["general"];
			if (!general) {
				ProjectSettings.SetSetting(name, Variant.CreateFrom<string>(null));
			}
		}
	}
}

#endif // TOOLS
