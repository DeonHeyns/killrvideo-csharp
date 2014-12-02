using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Cassandra;
using KillrVideo.Uploads.Dtos;
using KillrVideo.Uploads.Messages.Events;
using KillrVideo.Utils;
using Microsoft.WindowsAzure.MediaServices.Client;
using Nimbus;

namespace KillrVideo.Uploads
{
    /// <summary>
    /// Component responsible for reacing to changes in Azure Media Services encoding job states.
    /// </summary>
    public class EncodingJobMonitor : IMonitorEncodingJobs
    {
        private static readonly TimeSpan PublishedVideosGoodFor = TimeSpan.FromDays(10000);

        private readonly ISession _session;
        private readonly TaskCache<string, PreparedStatement> _statementCache;
        private readonly IBus _bus;
        private readonly CloudMediaContext _cloudMediaContext;
        private readonly Random _random;

        public EncodingJobMonitor(ISession session, TaskCache<string, PreparedStatement> statementCache, IBus bus, CloudMediaContext cloudMediaContext)
        {
            if (session == null) throw new ArgumentNullException("session");
            if (statementCache == null) throw new ArgumentNullException("statementCache");
            if (bus == null) throw new ArgumentNullException("bus");
            if (cloudMediaContext == null) throw new ArgumentNullException("cloudMediaContext");

            _session = session;
            _statementCache = statementCache;
            _bus = bus;
            _cloudMediaContext = cloudMediaContext;

            _random = new Random();
        }

        // ReSharper disable ReplaceWithSingleCallToFirstOrDefault
        // ReSharper disable ReplaceWithSingleCallToSingleOrDefault

        /// <summary>
        /// Handles an event/notification from Azure Media Services about an encoding job.
        /// </summary>
        public async Task HandleEncodingJobEvent(EncodingJobEvent notification)
        {
            string jobId = notification.GetJobId();

            // Lookup the uploaded video's Id by job Id
            PreparedStatement lookupPrepared =
                await _statementCache.NoContext.GetOrAddAsync("SELECT videoid FROM uploaded_video_jobs_by_jobid WHERE jobid = ?");
            RowSet lookupRows = await _session.ExecuteAsync(lookupPrepared.Bind(jobId));
            Row lookupRow = lookupRows.SingleOrDefault();
            if (lookupRow == null)
                throw new InvalidOperationException(string.Format("Could not find video for job id {0}", jobId));

            var videoId = lookupRow.GetValue<Guid>("videoid");

            // If the job isn't finished yet, just record the event and bail
            if (notification.IsJobFinished() == false)
            {
                await RecordEventInCassandra(notification, videoId);
                return;
            }
            
            // See if the finished job was successful
            if (notification.WasSuccessful() == false)
            {
                // Job failed so record it and notify the world
                await RecordEventInCassandra(notification, videoId);
                await _bus.Publish(new UploadedVideoProcessingFailed
                {
                    VideoId = videoId,
                    Timestamp = notification.TimeStamp
                });
                return;
            }

            // Job finished successfully, so do work necessary to publish it for playback

            // Find the job in Azure Media Services and throw if not found
            IJob job = _cloudMediaContext.Jobs.Where(j => j.Id == jobId).SingleOrDefault();
            if (job == null)
                throw new InvalidOperationException(string.Format("Could not find job {0}", jobId));

            List<IAsset> outputAssets = job.OutputMediaAssets.ToList();

            // Find the encoded video asset
            IAsset asset = outputAssets.SingleOrDefault(a => a.Name.StartsWith(UploadConfig.EncodedVideoAssetNamePrefix));
            if (asset == null)
                throw new InvalidOperationException(string.Format("Could not find video output asset for job {0}", jobId));

            // Publish the asset for progressive downloading (HTML5) by creating an SAS locator for it and adding the file name to the path
            ILocator locator = asset.Locators.Where(l => l.Type == LocatorType.Sas).FirstOrDefault();
            if (locator == null)
            {
                const AccessPermissions readPermissions = AccessPermissions.Read | AccessPermissions.List;
                locator = await _cloudMediaContext.Locators.CreateAsync(LocatorType.Sas, asset, readPermissions,
                                                                        PublishedVideosGoodFor);
            }

            // Get the URL for streaming from the locator (embed file name for the mp4 in locator before query string)
            IAssetFile mp4File = asset.AssetFiles.ToList().Single(f => f.Name.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase));
            var videoLocation = new UriBuilder(locator.Path);
            videoLocation.Path += "/" + mp4File.Name;

            // Find the thumbnail asset
            IAsset thumbnailAsset = outputAssets.SingleOrDefault(a => a.Name.StartsWith(UploadConfig.ThumbnailAssetNamePrefix));
            if (thumbnailAsset == null)
                throw new InvalidOperationException(string.Format("Could not find thumbnail output asset for job {0}", jobId));

            // Publish the thumbnail asset by creating a locator for it (again, check if already present)
            ILocator thumbnailLocator = thumbnailAsset.Locators.Where(l => l.Type == LocatorType.Sas).FirstOrDefault();
            if (thumbnailLocator == null)
            {
                thumbnailLocator = await _cloudMediaContext.Locators.CreateAsync(LocatorType.Sas, thumbnailAsset, AccessPermissions.Read,
                                                                                 PublishedVideosGoodFor);
            }

            // Get the URL for a random thumbnail file in the asset
            List<IAssetFile> jpgFiles =
                thumbnailAsset.AssetFiles.ToList().Where(f => f.Name.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)).ToList();
            var thumbnailLocation = new UriBuilder(thumbnailLocator.Path);
            int randomThumbnailIndex = _random.Next(jpgFiles.Count);
            thumbnailLocation.Path += "/" + jpgFiles[randomThumbnailIndex].Name;

            // Record the status and tell the world about the successful encoding job
            await RecordEventInCassandra(notification, videoId);
            await _bus.Publish(new UploadedVideoProcessingSucceeded
            {
                VideoId = videoId,
                VideoUrl = videoLocation.Uri.AbsoluteUri,
                ThumbnailUrl = thumbnailLocation.Uri.AbsoluteUri,
                Timestamp = notification.TimeStamp
            });
        }

        // ReSharper restore ReplaceWithSingleCallToFirstOrDefault
        // ReSharper restore ReplaceWithSingleCallToSingleOrDefault

        /// <summary>
        /// Records an encoding job event in Cassandra.
        /// </summary>
        private async Task RecordEventInCassandra(EncodingJobEvent notification, Guid videoId)
        {
            // Log the event to C* (this should be idempotent in case of dupliacte tries since we're keyed by the job id, date, and etag in C*)
            PreparedStatement preparedStatement = await _statementCache.NoContext.GetOrAddAsync(
                "INSERT INTO encoding_job_notifications (videoid, status_date, etag, jobId, newstate, oldstate) VALUES (?, ?, ?, ?, ?, ?)");

            string jobId = notification.GetJobId();
            string newState = notification.GetNewState();
            string oldState = notification.GetOldState();
            DateTimeOffset statusDate = notification.TimeStamp;
            
            // INSERT INTO encoding_job_notifications ...
            await _session.ExecuteAsync(
                preparedStatement.Bind(videoId, statusDate, notification.ETag, jobId, newState, oldState).SetTimestamp(statusDate));
        }
    }
}