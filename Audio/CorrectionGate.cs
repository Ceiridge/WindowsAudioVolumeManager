namespace WindowsAudioVolumeManager.Audio {
	/// <summary>
	/// Non-blocking rate guard for strict Maintain. The first deviation is corrected
	/// immediately on its Core Audio callback. Only subsequent writes to the SAME
	/// live session are paced to prevent a pathological feedback storm.
	/// A deferred correction must be retried by the one-shot worker timer even if
	/// no further volume-change event arrives. There is no permanent or multi-second
	/// pause, and no periodic polling of normally stable sessions.
	/// </summary>
	public sealed class CorrectionGate {
		public const float Tolerance = 0.002f;
		public const long MinimumWriteSpacingMilliseconds = 10;
		public const long FailedWriteRetryMilliseconds = 150;
		private long _lastWrite;
		private long _retryAt;
		private bool _hasWritten;
		public bool Pending { get; private set; }

		public long RetryAfterMilliseconds(long nowMilliseconds) =>
			Pending ? Math.Max(0, _retryAt - nowMilliseconds) : 0;

		/// <summary>Returns true only when an actual correction write is needed now.</summary>
		public bool TryCorrect(float actual, float target, long nowMilliseconds) {
			if (!float.IsFinite(actual) || !float.IsFinite(target)) { return false; }
			if (Math.Abs(actual - target) <= Tolerance) {
				Pending = false; // Includes echoes of our own writes.
				return false;
			}
			// Preserve a scheduled retry: callbacks must not postpone the deadline.
			if (Pending && nowMilliseconds < _retryAt) { return false; }
			if (_hasWritten && nowMilliseconds - _lastWrite < MinimumWriteSpacingMilliseconds) {
				Pending = true;
				_retryAt = _lastWrite + MinimumWriteSpacingMilliseconds;
				return false;
			}
			Pending = false;
			_hasWritten = true;
			_lastWrite = nowMilliseconds;
			return true;
		}

		/// <summary>Only COM failures have a longer retry; this never disables Maintain.</summary>
		public void MarkWriteFailed(long nowMilliseconds) {
			Pending = true;
			_retryAt = nowMilliseconds + FailedWriteRetryMilliseconds;
		}

		public void Reset() {
			Pending = false;
			_hasWritten = false;
			_lastWrite = _retryAt = 0;
		}
	}
}
