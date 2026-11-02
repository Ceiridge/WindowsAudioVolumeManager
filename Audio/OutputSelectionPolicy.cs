namespace WindowsAudioVolumeManager.Audio {
	public static class OutputSelectionPolicy {
		public static bool ShouldManage(string id, bool deviceScoped, string? selectedId,
			IReadOnlySet<string> explicitlyManagedIds, bool followDefault, string? defaultId) =>
			!string.IsNullOrEmpty(id) && (deviceScoped
				? explicitlyManagedIds.Contains(id) || (followDefault && id == defaultId)
				: id == selectedId);
	}
}
