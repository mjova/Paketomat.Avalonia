using Flatsoft.Common.UI;
using LibVLCSharp.Shared;
using Microsoft.Extensions.Logging;

namespace TopPos.Platform
{
	/// <summary>
	/// Zvuk kioska preko LibVLC-a, isti kod na Windows-u i Linuxu (v2 AudioPlayer, WPF MediaPlayer). Fajlovi idu u red i puštaju
	/// se jedan za drugim (<see cref="AudioQueue"/>); <see cref="Reset"/> zaustavlja i prazni red. Biblioteka: na Windows-u iz
	/// paketa <c>VideoLAN.LibVLC.Windows</c> uz program, na Linuxu sistemska (<c>libvlc5</c>, <c>vlc-plugin-base</c>).
	/// Svako puštanje je svoj <see cref="Playback"/>: kraj puštanja se vezuje za njega, pa zakasneo kraj ranijeg puštanja ne prekida
	/// tekuće. LibVLC događaje šalje sa svoje niti, iz koje se LibVLC ne sme pozivati — obrada ide na pool nit.
	/// </summary>
	public sealed class VlcAudioPlayer : IAudioPlayer, IDisposable
	{
		private readonly string _basePath;
		private readonly ILogger? _logger;
		private readonly LibVLC _libVlc;
		private readonly MediaPlayer _player;
		private readonly AudioQueue _queue;

		/// <summary>Baca izuzetak ako LibVLC nije dostupan (vidi <see cref="PlatformServices.CreateAudioPlayer"/>).</summary>
		public VlcAudioPlayer(string basePath, ILogger? logger)
		{
			_basePath = basePath;
			_logger = logger;
			Core.Initialize();
			_libVlc = new LibVLC("--no-video", "--quiet");
			_player = new MediaPlayer(_libVlc);
			_queue = new AudioQueue(StartPlayback, () => _player.Stop(), playback => ((Playback) playback).Dispose(), logger);
		}

		/// <summary>Da li red i puštanje miruju (za testove).</summary>
		internal bool IsIdle => _queue.IsIdle;

		public void Play(string audioFileName)
		{
			string audioFilePath = Path.Combine(_basePath, audioFileName);
			if (!File.Exists(audioFilePath)) {
				_logger?.LogError($"{nameof(VlcAudioPlayer)}: {audioFilePath} not found.");
				return;
			}
			_logger?.LogDebug($"{nameof(VlcAudioPlayer)}: adding {audioFilePath} to the queue.");
			_queue.Enqueue(audioFilePath);
		}

		public void Reset()
		{
			_queue.Clear();
			_logger?.LogDebug($"{nameof(VlcAudioPlayer)}: {nameof(Reset)}: the queue is cleared.");
		}

		private object? StartPlayback(string path)
		{
			var playback = new Playback(new Media(_libVlc, path, FromType.FromPath), (playback, state) => {
				if (state == VLCState.Error) {
					_logger?.LogError($"{nameof(VlcAudioPlayer)}: {path} nije pušten do kraja (greška LibVLC-a).");
				}
				ThreadPool.QueueUserWorkItem(_ => _queue.Finished(playback));
			});
			try {
				if (_player.Play(playback.Media)) {     // ne blokira; plejer drži svoju referencu na media
					_logger?.LogDebug($"{nameof(VlcAudioPlayer)}: playing {path}.");
					return playback;
				}
				_logger?.LogError($"{nameof(VlcAudioPlayer)}: {path} could not be played.");
			}
			catch {
				playback.Dispose();
				throw;
			}
			playback.Dispose();
			return null;
		}

		public void Dispose()
		{
			_queue.Clear(close: true);
			_player.Dispose();
			_libVlc.Dispose();
		}

		/// <summary>
		/// Jedno puštanje: medij i pretplata na njegov kraj. LibVLCSharp drži svaku pretplatu (GCHandle) dok se ne odjavi, pa se
		/// pri oslobađanju prvo odjavljuje, pa oslobađa medij.
		/// </summary>
		private sealed class Playback : IDisposable
		{
			private readonly EventHandler<MediaStateChangedEventArgs> _onStateChanged;
			private int _disposed;

			public Playback(Media media, Action<Playback, VLCState> onEnded)
			{
				Media = media;
				_onStateChanged = (_, e) => {
					if (e.State is VLCState.Ended or VLCState.Error) {
						onEnded(this, e.State);
					}
				};
				Media.StateChanged += _onStateChanged;
			}

			public Media Media { get; }

			public void Dispose()
			{
				if (Interlocked.Exchange(ref _disposed, 1) != 0) {
					return;
				}
				Media.StateChanged -= _onStateChanged;
				Media.Dispose();
			}
		}
	}
}