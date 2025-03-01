// Test.cs

using Godot;
using System;

public partial class Test : Node {
	public override void _Ready() {
		if (Analytics.ShouldShowConsentDialog()) {
			Analytics.ShowConsentDialog(this);
		}
	}

	public void _OnButtonPressed() {
		Analytics.HandleExit();
		GetTree().Quit();
	}

	public void _OnTestEventButtonPressed() {
		Analytics.AddEvent("Test event");
	}
}
