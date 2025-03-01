// ConsentDialog.cs

using Godot;
using System;

[Tool]
public partial class ConsentDialog : CanvasLayer {
	[Export]
	public AnimationPlayer animPlayer;

	public void ShowWithAnimation(string name = "pop_up") {
		animPlayer.Play(name);
	}

	public void HideWithAnimation(string name = "pop_up") {
		animPlayer.PlayBackwards(name);
	}

	public void OnApproveButtonPressed() {
		Analytics.ApproveDataCollection();
		Hide();
	}

	public void OnDenyButtonPressed() {
		Analytics.DenyDataCollection();
		Hide();
	}
}
