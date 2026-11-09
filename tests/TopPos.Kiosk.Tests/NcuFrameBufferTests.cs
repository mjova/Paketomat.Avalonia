using TopPos.LockController.NCU16L;
using TopPos.LockController.NCU24L;
using Xunit;

namespace TopPos.Workflow.Tests
{
	/// <summary>
	/// Prijemni bafer NCU kontrolera čita okvire redom, od granice okvira. Nizovi u testovima sudara su iz Codex review-a
	/// (2026-10-16): pri traženju okvira bajt po bajt, ostatak zastarelog okvira spojen sa početkom pravog odgovora daje
	/// kandidat sa ispravnom jednobajtnom sumom — čak i sa ispravnom adresom i komandom — pa bi bilo pročitano pogrešno
	/// stanje vrata, a početak pravog odgovora izgubljen.
	/// </summary>
	public class NcuFrameBufferTests
	{
		private static byte[] Bytes(string hex) => Convert.FromHexString(hex.Replace(" ", ""));

		private static void Append(NCU16LFrameBuffer buffer, byte[] data) => buffer.Append(data, data.Length);

		private static void Append(NCU24LFrameBuffer buffer, byte[] data) => buffer.Append(data, data.Length);

		[Fact]
		public void Ncu16l_stale_frame_rest_is_discarded_as_a_whole_not_read_as_matching_false_frame()
		{
			// Stari okvir „02 03 35 02 03 35 3E 03 B5": skeniranjem bi „02 03 35 3E 03 B5 02 03 35" prošao sumu, adresu i komandu.
			var old = Bytes("02 03 35 02 03 35 3E 03 B5");
			var real = Bytes("02 03 35 00 00 00 00 03 3D");
			var buffer = new NCU16LFrameBuffer();

			Append(buffer, old[..1]);
			buffer.DiscardStale();
			Append(buffer, [.. old[1..], .. real]);

			Assert.Equal(real, buffer.TakeNextFrame());
			Assert.Null(buffer.TakeNextFrame());
			Assert.Equal(0, buffer.Count);
		}

		[Fact]
		public void Ncu24l_stale_frame_rest_is_discarded_as_a_whole_not_read_as_false_frame()
		{
			// Stari okvir: skeniranjem bi „02 00 80 10 03 03 03 03 66 00 02" prošao sumu i odneo STX pravog odgovora.
			var old = Bytes("02 02 00 80 10 03 03 03 03 66 00");
			var real = Bytes("02 02 00 80 10 03 03 97 FF FF FF");
			var buffer = new NCU24LFrameBuffer();

			Append(buffer, old[..1]);
			buffer.DiscardStale();
			Append(buffer, [.. old[1..], .. real]);

			Assert.Equal(real, buffer.TakeNextFrame());
			Assert.Null(buffer.TakeNextFrame());
		}

		[Fact]
		public void Ncu24l_stale_frame_with_partial_header_waits_for_its_length_and_is_discarded()
		{
			var old = Bytes("02 00 00 80 10 03 03 98 00 00 00");
			var real = Bytes("02 00 02 81 10 00 03 98");
			var buffer = new NCU24LFrameBuffer();

			Append(buffer, old[..3]);
			buffer.DiscardStale();
			Append(buffer, old[3..5]);
			Assert.Null(buffer.TakeNextFrame());
			Append(buffer, [.. old[5..], .. real]);

			Assert.Equal(real, buffer.TakeNextFrame());
		}

		[Fact]
		public void Complete_stale_frames_are_discarded_and_buffer_is_empty()
		{
			var buffer = new NCU24LFrameBuffer();
			Append(buffer, Bytes("02 00 00 80 10 03 03 98 00 00 00 02 00 00 80 10 03 03 98 00 00 00"));

			buffer.DiscardStale();

			Assert.Equal(0, buffer.Count);
			Assert.Null(buffer.TakeNextFrame());
		}

		[Fact]
		public void Bu_prefix_before_frame_is_skipped()
		{
			var frame = Bytes("02 00 00 80 10 03 03 55 BF FF FF");
			var buffer = new NCU24LFrameBuffer();

			Append(buffer, [0xA0, 0xFE, 0x00, .. frame]);

			Assert.Equal(frame, buffer.TakeNextFrame());
		}

		[Fact]
		public void Corrupted_stx_is_not_skipped_into_middle_of_frame()
		{
			// Codex: oštećen i STX. Preskakanjem do sledećeg 0x02 bio bi pročitan lažni „02 00 35 3C 02 00 35 03 AD".
			var corrupted = Bytes("00 00 35 00 00 02 00 35 3C");
			var real = Bytes("02 00 35 03 AD 00 00 03 EA");
			var buffer = new NCU16LFrameBuffer();

			Append(buffer, [.. corrupted, .. real]);

			Assert.Null(buffer.TakeNextFrame());
			Assert.True(buffer.IsDesynchronized);
		}

		[Fact]
		public void Bu_prefix_is_accepted_only_before_first_frame_on_connection()
		{
			var frame = Bytes("02 00 35 01 00 00 00 03 3B");
			var buffer = new NCU16LFrameBuffer();
			Append(buffer, [0x00, .. frame]);
			Assert.Equal(frame, buffer.TakeNextFrame());

			Append(buffer, [0x00, .. frame]);

			Assert.Null(buffer.TakeNextFrame());
			Assert.True(buffer.IsDesynchronized);

			buffer.Resynchronize();
			Append(buffer, frame);
			Assert.Equal(frame, buffer.TakeNextFrame());

			buffer.Clear();
			Append(buffer, [0x00, .. frame]);
			Assert.Equal(frame, buffer.TakeNextFrame());
		}

		[Fact]
		public void Bu_prefix_waiting_before_first_command_is_not_marked_as_stale_frame()
		{
			// Codex: prefiks stigne pre prve komande; posle pražnjenja odgovor na komandu ne sme da bude odbačen kao zastareo.
			var frame16 = Bytes("02 00 35 01 00 00 00 03 3B");
			var buffer16 = new NCU16LFrameBuffer();
			Append(buffer16, [0x00]);
			buffer16.DiscardStale();
			Append(buffer16, frame16);
			Assert.Equal(frame16, buffer16.TakeNextFrame());

			var frame24 = Bytes("02 00 00 80 10 03 03 55 BF FF FF");
			var buffer24 = new NCU24LFrameBuffer();
			Append(buffer24, [0xA0, 0xFE, 0x00]);
			buffer24.DiscardStale();
			Append(buffer24, frame24);
			Assert.Equal(frame24, buffer24.TakeNextFrame());
		}

		[Fact]
		public void Buffer_stays_bounded_under_continuous_input_and_recovers_after_resynchronize()
		{
			// Codex: pri trajnom šumu DoorWatcher ponavlja pokušaje; bafer ne sme da raste bez granice.
			var frame = Bytes("02 00 35 01 00 00 00 03 3B");
			var buffer = new NCU16LFrameBuffer();
			var noise = Enumerable.Repeat((byte)0x02, 1000).ToArray();

			for (int attempt = 0; attempt < 20; attempt++)
			{
				Append(buffer, noise);
				buffer.DiscardStale();
				Assert.True(buffer.Count <= NCU16LFrameBuffer.MaxBufferedBytes);
			}

			Assert.True(buffer.IsDesynchronized);
			Assert.Equal(0, buffer.Count);

			buffer.Resynchronize();
			Append(buffer, frame);
			Assert.Equal(frame, buffer.TakeNextFrame());
		}

		[Fact]
		public void Frame_arriving_in_parts_is_returned_when_complete()
		{
			var frame = Bytes("02 00 35 01 00 00 00 03 3B");
			var buffer = new NCU16LFrameBuffer();

			Append(buffer, frame[..4]);
			Assert.Null(buffer.TakeNextFrame());
			Append(buffer, frame[4..]);

			Assert.Equal(frame, buffer.TakeNextFrame());
		}

		[Fact]
		public void Corrupted_frame_desynchronizes_buffer_and_no_false_frame_is_read_from_it()
		{
			// Codex: okvir sa oštećenim ETX, pa pravi odgovor. Traženjem granice unutar oštećenih bajtova bio bi pročitan
			// lažni „02 00 35 3C 02 00 35 03 AD" (ispravna suma, adresa i komanda) sa stanjem 3C 02 umesto 03 AD.
			var corrupted = Bytes("02 00 35 00 00 02 00 35 3C");
			var real = Bytes("02 00 35 03 AD 00 00 03 EA");
			var buffer = new NCU16LFrameBuffer();

			Append(buffer, [.. corrupted, .. real]);

			Assert.Null(buffer.TakeNextFrame());
			Assert.True(buffer.IsDesynchronized);
			Append(buffer, real);
			Assert.Null(buffer.TakeNextFrame());

			buffer.Clear();
			Append(buffer, real);

			Assert.False(buffer.IsDesynchronized);
			Assert.Equal(real, buffer.TakeNextFrame());
		}

		[Fact]
		public void Clear_forgets_kept_stale_frame()
		{
			var real = Bytes("02 00 35 01 00 00 00 03 3B");
			var buffer = new NCU16LFrameBuffer();

			Append(buffer, real[..2]);
			buffer.DiscardStale();
			buffer.Clear();
			Append(buffer, real);

			Assert.Equal(real, buffer.TakeNextFrame());
		}
	}
}
