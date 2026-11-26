using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace TopPos.Platform.Linux
{
	/// <summary>
	/// Tip, prava, vlasnik i inode unosa bez praćenja linka (<c>statx</c> sa <c>AT_SYMLINK_NOFOLLOW</c>). .NET ne daje tip unosa
	/// (socket, link), vlasnika ni inode; <c>struct statx</c> ima isti raspored na svim arhitekturama, za razliku od
	/// <c>struct stat</c>.
	/// </summary>
	[SupportedOSPlatform("linux")]
	internal readonly partial record struct LinuxFileStatus(uint Type, uint Permissions, uint Uid, ulong Inode)
	{
		public const uint Directory = 0x4000;   // S_IFDIR
		public const uint Socket = 0xC000;      // S_IFSOCK

		private const int __atFdCwd = -100;
		private const int __atSymlinkNoFollow = 0x100;
		private const uint __statxType = 0x1, __statxMode = 0x2, __statxUid = 0x8, __statxIno = 0x100;
		private const uint __required = __statxType | __statxMode | __statxUid | __statxIno;
		private const int __eNoEnt = 2;

		/// <summary><c>null</c> = unos ne postoji; druga greška (i polje koje jezgro nije vratilo) baca <see cref="IOException"/>.</summary>
		public static LinuxFileStatus? Get(string path)
		{
			if (statx(__atFdCwd, path, __atSymlinkNoFollow, __required, out var buffer) != 0) {
				int errno = Marshal.GetLastPInvokeError();
				if (errno == __eNoEnt) {
					return null;
				}
				throw new IOException($"statx({path}) nije uspeo (errno {errno}).");
			}
			if ((buffer.Mask & __required) != __required) {
				throw new IOException($"statx({path}) nije vratio tip, prava, vlasnika i inode (maska 0x{buffer.Mask:X}).");
			}
			return new LinuxFileStatus(buffer.Mode & 0xF000u, buffer.Mode & 0xFFFu, buffer.Uid, buffer.Inode);
		}

		// Početak struct statx (linux/stat.h); ukupna veličina je 256 bajtova.
		[StructLayout(LayoutKind.Sequential, Size = 256)]
		private struct StatxBuffer
		{
			public uint Mask;
			public uint BlockSize;
			public ulong Attributes;
			public uint LinkCount;
			public uint Uid;
			public uint Gid;
			public ushort Mode;
			public ushort Spare0;
			public ulong Inode;
		}

		[LibraryImport("libc", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
		private static partial int statx(int directoryFd, string path, int flags, uint mask, out StatxBuffer buffer);
	}
}
