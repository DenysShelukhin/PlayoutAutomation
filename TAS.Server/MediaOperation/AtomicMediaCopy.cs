// EWTN 2026-10-30. Native file-operation support, GPL-2.0.
using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace TAS.Server.MediaOperation
{
    internal static class AtomicMediaCopy
    {
        internal static string Hash(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "");
        }

        internal static async Task Copy(string source, string destination, Guid guid,
            CancellationToken token, Action<int> progress)
        {
            if (string.Equals(Path.GetFullPath(source), Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase))
                throw new IOException("Archive transfer source and destination are identical.");
            if (File.Exists(destination))
            {
                if (new FileInfo(source).Length == new FileInfo(destination).Length && Hash(source) == Hash(destination)) return;
                throw new IOException("Destination already exists with different content; it will not be overwritten.");
            }
            Directory.CreateDirectory(Path.GetDirectoryName(destination));
            // Same volume as final file; extension is excluded by the native media watcher.
            var staging = destination + ".ewtn-" + guid.ToString("N") + ".partial";
            try
            {
                string sourceHash;
                using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, true))
                using (var output = new FileStream(staging, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024, true))
                using (var sha = SHA256.Create())
                {
                    var buffer = new byte[1024 * 1024];
                    long count = 0;
                    int n;
                    while ((n = await input.ReadAsync(buffer, 0, buffer.Length, token).ConfigureAwait(false)) > 0)
                    {
                        await output.WriteAsync(buffer, 0, n, token).ConfigureAwait(false);
                        sha.TransformBlock(buffer, 0, n, buffer, 0);
                        count += n;
                        progress?.Invoke((int)(count * 99 / Math.Max(1, input.Length)));
                    }
                    sha.TransformFinalBlock(new byte[0], 0, 0);
                    sourceHash = BitConverter.ToString(sha.Hash).Replace("-", "");
                    await output.FlushAsync(token).ConfigureAwait(false);
                    output.Flush(true);
                }
                token.ThrowIfCancellationRequested();
                if (Hash(staging) != sourceHash) throw new IOException("Archive transfer SHA256 verification failed.");
                File.SetCreationTimeUtc(staging, File.GetCreationTimeUtc(source));
                File.SetLastWriteTimeUtc(staging, File.GetLastWriteTimeUtc(source));
                token.ThrowIfCancellationRequested();
                File.Move(staging, destination); // no overwrite, atomic publication on this volume
            }
            finally
            {
                if (File.Exists(staging)) File.Delete(staging); // only this operation's disposable partial
            }
        }
    }
}
