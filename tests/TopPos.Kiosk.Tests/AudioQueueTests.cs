using TopPos.Platform;
using Xunit;

namespace TopPos.Kiosk.Tests
{
	/// <summary>v4: red zvukova (v2 AudioPlayer) bez LibVLC-a; kraj puštanja dolazi kad ga test pošalje.</summary>
	public class AudioQueueTests
	{
		private sealed class Playback(string path)
		{
			public string Path { get; } = path;
		}

		private readonly List<Playback> _started = new();
		private readonly List<Playback> _released = new();
		private int _stops;
		private readonly AudioQueue _queue;

		public AudioQueueTests()
			=> _queue = new AudioQueue(path => {
										if (path.StartsWith("bad")) {
											return null;
										}
										var playback = new Playback(path);
										_started.Add(playback);
										return playback;
									},
									() => _stops++,
									playback => _released.Add((Playback) playback),
									logger: null);

		[Fact]
		public void Files_play_one_after_another()
		{
			_queue.Enqueue("a");
			_queue.Enqueue("b");
			Assert.Equal(["a"], _started.Select(p => p.Path));

			_queue.Finished(_started[0]);
			Assert.Equal(["a", "b"], _started.Select(p => p.Path));
			Assert.Equal([_started[0]], _released);

			_queue.Finished(_started[1]);
			Assert.True(_queue.IsIdle);
		}

		/// <summary>Kraj puštanja A stigne posle Reset-a i početka B: B se ne prekida i plejer ne postaje „slobodan“.</summary>
		[Fact]
		public void Late_end_of_playback_before_reset_does_not_touch_the_new_one()
		{
			_queue.Enqueue("a");
			var a = _started[0];
			_queue.Clear();
			_queue.Enqueue("b");
			_queue.Enqueue("c");

			_queue.Finished(a);

			Assert.Equal(["a", "b"], _started.Select(p => p.Path));     // c čeka kraj b
			Assert.False(_queue.IsIdle);
			Assert.Equal(1, _stops);
			Assert.Equal([a], _released);
		}

		[Fact]
		public void Clear_stops_current_and_empties_queue()
		{
			_queue.Enqueue("a");
			_queue.Enqueue("b");

			_queue.Clear();

			Assert.True(_queue.IsIdle);
			Assert.Equal(1, _stops);
			Assert.Equal([_started[0]], _released);
			_queue.Finished(_started[0]);
			Assert.Single(_started);
		}

		[Fact]
		public void Clear_when_idle_does_not_stop_player()
		{
			_queue.Clear();
			Assert.Equal(0, _stops);
		}

		[Fact]
		public void File_that_cannot_be_started_is_skipped()
		{
			_queue.Enqueue("bad");
			Assert.True(_queue.IsIdle);
			_queue.Enqueue("a");
			Assert.Equal(["a"], _started.Select(p => p.Path));
		}

		[Fact]
		public void Closed_queue_accepts_no_files()
		{
			_queue.Enqueue("a");
			_queue.Clear(close: true);
			_queue.Enqueue("b");
			_queue.Finished(_started[0]);

			Assert.Single(_started);
			Assert.True(_queue.IsIdle);
		}
	}
}