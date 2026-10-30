using jNet.RPC;
using System;
using System.IO;
using System.Threading.Tasks;
using TAS.Common;
using TAS.Common.Interfaces;
using TAS.Common.Interfaces.Media;
using TAS.Common.Interfaces.MediaDirectory;
using TAS.Server.Media;

namespace TAS.Server.MediaOperation
{
    public class CopyOperation : FileOperationBase, ICopyOperation
    {
        private readonly object _destMediaLock = new object();

        private IMedia _sourceMedia;

        [DtoMember]
        public IMediaDirectory DestDirectory { get; set; }

        [DtoMember]
        public IMedia Source { get => _sourceMedia; set => SetField(ref _sourceMedia, value); }

        internal MediaBase Dest { get; set; }
        internal bool AtomicPublication { get; set; }
        internal bool IsScheduledRestore { get; set; }

        // utility methods

        protected virtual void CreateDestMediaIfNotExists()
        {
            lock (_destMediaLock)
            {
                if (Dest != null)
                    return;
                if (!(DestDirectory is MediaDirectoryBase mediaDirectory))
                    throw new ApplicationException($"Cannot create destination media on {DestDirectory}");
                // Reuse a missing placeholder, preserving GUID and event references.
                Dest = Source is ArchiveMedia && mediaDirectory is ServerDirectory server
                    ? server.FindMediaByMediaGuid(Source.MediaGuid) : null;
                if (Dest == null) Dest = (MediaBase)mediaDirectory.CreateMedia(Source);
            }
        }

        protected override void OnOperationStatusChanged()
        {
            TIngestStatus newIngestStatus;
            switch (OperationStatus)
            {
                case FileOperationStatus.Finished:
                    newIngestStatus = TIngestStatus.Ready;
                    break;
                case FileOperationStatus.Waiting:
                case FileOperationStatus.InProgress:
                    newIngestStatus = TIngestStatus.InProgress;
                    break;
                default:
                    newIngestStatus = TIngestStatus.NotReady;
                    break;
            }
            if (DestDirectory is ServerDirectory serverDirectory)
            {
                if (_sourceMedia is IngestMedia im)
                    im.NotifyIngestStatusUpdated(serverDirectory, newIngestStatus);
                if (_sourceMedia is ArchiveMedia am)
                    am.NotifyIngestStatus(serverDirectory, newIngestStatus);
            }
        }

        public override void Abort()
        {
            base.Abort();
            if (AtomicPublication) return; // never delete a published complete file on cancellation
            lock (_destMediaLock)
            {
                if (Dest != null && Dest.FileExists())
                    Dest.Delete();
            }
        }

        protected override async Task<bool> InternalExecute()
        {
            StartTime = DateTime.UtcNow;
            if (!(Source is MediaBase source))
                return false;
            if (!File.Exists(source.FullPath) || !Directory.Exists(DestDirectory.Folder))
                return false;
            CreateDestMediaIfNotExists();
            FileUtils.CreateDirectoryIfNotExists(Path.GetDirectoryName(Dest.FullPath));
            if (AtomicPublication)
            {
                Dest.IsVerified = false;
                Dest.MediaStatus = TMediaStatus.Copying;
                if (Dest is ServerMedia placeholder)
                {
                    placeholder.Save(); // durable GUID before publishing any playable filename
                    if (placeholder.IdPersistentMedia == 0) throw new IOException("Cannot persist restore placeholder.");
                }
                await AtomicMediaCopy.Copy(source.FullPath, Dest.FullPath, source.MediaGuid,
                    CancellationTokenSource.Token, p => Progress = p).ConfigureAwait(false);
                // Once atomically published, retain and verify the complete cache even if
                // the requesting event was removed at the publication boundary.
                Dest.MediaStatus = TMediaStatus.Copied;
                await Task.Run(() => Dest.Verify(false)).ConfigureAwait(false);
                if (Dest.MediaStatus != TMediaStatus.Available || !Dest.FileExists() ||
                    (ulong)new FileInfo(Dest.FullPath).Length != Source.FileSize)
                    throw new IOException("Copied media is not verified Ready; local/archived sources retained.");
                (Dest as PersistentMedia)?.Save();
                if (Dest is ArchiveMedia && DestDirectory is ArchiveDirectory archive &&
                    (!(archive.Find(Dest.MediaGuid) is ArchiveMedia stored) || stored.FullPath != Dest.FullPath || stored.MediaStatus != TMediaStatus.Available))
                    throw new IOException("Archive file copied but database registration not confirmed; local copy retained.");
                ((MediaDirectoryBase)DestDirectory).RefreshVolumeInfo();
                return true;
            }
            if (!(Dest.FileExists()
                  && File.GetLastWriteTimeUtc(source.FullPath)
                      .Equals(File.GetLastWriteTimeUtc(Dest.FullPath))
                  && File.GetCreationTimeUtc(source.FullPath).Equals(File.GetCreationTimeUtc(Dest.FullPath))
                  && Source.FileSize.Equals(Dest.FileSize)))
            {
                Dest.MediaStatus = TMediaStatus.Copying;
                IsIndeterminate = true;
                if (!await source.CopyMediaTo(Dest, CancellationTokenSource.Token))
                {
                    Dest.Delete();
                    return false;
                }
            }
            Dest.MediaStatus = TMediaStatus.Copied;
            await Task.Run(() => Dest.Verify(false));
            ((MediaDirectoryBase) DestDirectory).RefreshVolumeInfo();
            return true;
        }

        public override string ToString()
        {
            return $"Copy {Source} -> {DestDirectory}";
        }
    }
}
