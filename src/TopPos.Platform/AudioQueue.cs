using Microsoft.Extensions.Logging;

namespace TopPos.Platform
{
	/// <summary>
	/// Red zvukova, nezavisan od plejera (testira se bez LibVLC-a). Fajlovi se puštaju redom; svako puštanje ima svoj objekat
	/// (<paramref name="start"/>), pa kraj puštanja koji stigne posle <see cref="Clear"/> ili za ranije puštanje ne dira tekuće.
	/// <paramref name="start"/> i <paramref name="stop"/> se pozivaju pod zaključavanjem reda; ne smeju sinhrono da zovu red.
	/// </summary>
	/// <param name="start">Pokreće puštanje fajla i vraća njegov objekat; <c>null</c> = nije pokrenuto.</param>
	/// <param name="stop">Zaustavlja tekuće puštanje.</param>
	/// <param name="release">Oslobađa objekat puštanja koje više nije tekuće (završeno ili prekinuto).</param>
	internal sealed class AudioQueue(Func<string, object?> start, Action stop, Action<object> release, ILogger? logger)
	{
		private readonly object _gate = new();
		private readonly Queue<string> _files = new();
		private object? _current;
		private bool _closed;

		public bool IsIdle
		{
			get {
				lock (_gate) {
					return _current == null && _files.Count == 0;
				}
			}
		}

		public void Enqueue(string path)
		{
			lock (_gate) {
				if (_closed) {
					return;
				}
				_files.Enqueue(path);
				if (_current == null) {
					StartNext();
				}
			}
		}

		/// <summary>Kraj puštanja (završeno ili greška); za puštanje koje više nije tekuće ne radi ništa.</summary>
		public void Finished(object playback)
		{
			lock (_gate) {
				if (!ReferenceEquals(playback, _current)) {
					return;
				}
				_current = null;
				release(playback);
				if (!_closed) {
					StartNext();
				}
			}
		}

		/// <summary>Zaustavlja tekuće puštanje i prazni red; <paramref name="close"/> = red više ne prima fajlove.</summary>
		public void Clear(bool close = false)
		{
			lock (_gate) {
				_closed |= close;
				_files.Clear();
				if (_current is { } current) {
					_current = null;
					try {
						stop();
					}
					catch (Exception ex) {
						logger?.LogError($"{nameof(AudioQueue)}: zaustavljanje zvuka nije uspelo: {ex.Message}");
					}
					release(current);
				}
			}
		}

		// Samo pod _gate. Fajl koji ne može da se pusti se preskače.
		private void StartNext()
		{
			while (_files.TryDequeue(out var path)) {
				try {
					_current = start(path);
				}
				catch (Exception ex) {
					_current = null;
					logger?.LogError($"{nameof(AudioQueue)}: {path} nije pušten: {ex.Message}");
				}
				if (_current != null) {
					return;
				}
			}
		}
	}
}