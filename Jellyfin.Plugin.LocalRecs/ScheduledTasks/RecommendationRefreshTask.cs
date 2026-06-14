using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.LocalRecs.Configuration;
using Jellyfin.Plugin.LocalRecs.Models;
using Jellyfin.Plugin.LocalRecs.Services;
using Jellyfin.Plugin.LocalRecs.VirtualLibrary;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LocalRecs.ScheduledTasks
{
    /// <summary>
    /// Scheduled task for refreshing recommendations for all users.
    /// Can be triggered manually or scheduled via Jellyfin's task scheduler.
    /// </summary>
    public class RecommendationRefreshTask : IScheduledTask
    {
        private readonly ILogger<RecommendationRefreshTask> _logger;
        private readonly IUserManager _userManager;
        private readonly RecommendationRefreshService _refreshService;
        private readonly VirtualLibraryManager _virtualLibraryManager;
        private readonly DiagnosticLogService _diagnosticLogService;
        private readonly ITaskManager _taskManager;
        private readonly PlayStatusSyncService _playStatusSyncService;

        /// <summary>
        /// Initializes a new instance of the <see cref="RecommendationRefreshTask"/> class.
        /// </summary>
        /// <param name="logger">Logger instance.</param>
        /// <param name="userManager">User manager.</param>
        /// <param name="refreshService">Recommendation refresh service.</param>
        /// <param name="virtualLibraryManager">Virtual library manager.</param>
        /// <param name="diagnosticLogService">Diagnostic log service for appending post-sync results.</param>
        /// <param name="taskManager">Task manager for triggering library scans.</param>
        /// <param name="playStatusSyncService">Play status sync service for flushing pending syncs before scoring.</param>
        public RecommendationRefreshTask(
            ILogger<RecommendationRefreshTask> logger,
            IUserManager userManager,
            RecommendationRefreshService refreshService,
            VirtualLibraryManager virtualLibraryManager,
            DiagnosticLogService diagnosticLogService,
            ITaskManager taskManager,
            PlayStatusSyncService playStatusSyncService)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _userManager = userManager ?? throw new ArgumentNullException(nameof(userManager));
            _refreshService = refreshService ?? throw new ArgumentNullException(nameof(refreshService));
            _virtualLibraryManager = virtualLibraryManager ?? throw new ArgumentNullException(nameof(virtualLibraryManager));
            _diagnosticLogService = diagnosticLogService ?? throw new ArgumentNullException(nameof(diagnosticLogService));
            _taskManager = taskManager ?? throw new ArgumentNullException(nameof(taskManager));
            _playStatusSyncService = playStatusSyncService ?? throw new ArgumentNullException(nameof(playStatusSyncService));
        }

        /// <inheritdoc />
        public string Name => "Refresh Local Recommendations";

        /// <inheritdoc />
        public string Key => "LocalRecsRefresh";

        /// <inheritdoc />
        public string Description => "Updates personalized recommendations for all users based on watch history and metadata similarity";

        /// <inheritdoc />
        public string Category => "Local Recommendations";

        /// <inheritdoc />
        public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        {
            _logger.LogInformation("Starting recommendation refresh task (Build: {BuildVersion})", Plugin.BuildVersion);
            var startTime = DateTime.UtcNow;

            try
            {
                var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();

                // Step 1: Get all users (5% progress)
                progress?.Report(0);
                cancellationToken.ThrowIfCancellationRequested();
                var users = _userManager.GetUsers().ToList();
                _logger.LogInformation("Generating recommendations for {UserCount} users", users.Count);
                progress?.Report(5);

                if (users.Count == 0)
                {
                    _logger.LogWarning("No users found, skipping recommendation refresh");
                    progress?.Report(100);
                    return;
                }

                // Step 2: Flush any pending virtual-to-source syncs (IsFavorite, play state) before scoring.
                // All virtual library folders still exist at this point (rebuild hasn't run yet), so
                // TrySyncSeriesFavoriteToSource can enumerate episode symlinks for series in any library type.
                _playStatusSyncService.Flush();

                var userIds = users.Select(u => u.Id).ToList();
                var (userRecommendations, leavingSoonState, allItems) = await _refreshService.GenerateRecommendationsForMultipleUsersAsync(
                    userIds,
                    config,
                    startTime).ConfigureAwait(false);

                progress?.Report(80);

                // Step 3: Sync virtual libraries for each user (80-90% progress)
                cancellationToken.ThrowIfCancellationRequested();
                var successfulUsers = 0;
                var failedUsers = new List<string>();

                foreach (var user in users)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    try
                    {
                        if (userRecommendations.TryGetValue(user.Id, out var recs))
                        {
                            _logger.LogDebug("Syncing virtual library for user {UserName} ({UserId})", user.Username, user.Id);

                            _virtualLibraryManager.SyncRecommendations(
                                user.Id,
                                recs.Movies,
                                MediaType.Movie);

                            _virtualLibraryManager.SyncRecommendations(
                                user.Id,
                                recs.Tv,
                                MediaType.Series);

                            successfulUsers++;
                            _logger.LogDebug(
                                "Successfully updated virtual library for {UserName}: {MovieCount} movies, {TvCount} TV shows",
                                user.Username,
                                recs.Movies.Count,
                                recs.Tv.Count);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to sync virtual library for user {UserName} ({UserId})", user.Username, user.Id);
                        failedUsers.Add(user.Username);
                    }
                }

                if (leavingSoonState != null)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    _virtualLibraryManager.SyncLeavingSoon(leavingSoonState, allItems);
                }

                progress?.Report(90);

                // Step 4: Trigger library scan and wait, then check for stale DB entries (90-95% progress)
                _logger.LogDebug("Waiting for file system flush before triggering library scan");
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
                await TriggerAndAwaitLibraryScanAsync(cancellationToken).ConfigureAwait(false);

                try
                {
                    var staleReport = _virtualLibraryManager.BuildStaleItemsReport(
                        users.Select(u => (u.Id, u.Username ?? "Unknown")),
                        userRecommendations,
                        leavingSoonState,
                        allItems);
                    _diagnosticLogService.Append(staleReport);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to build stale items report");
                }

                progress?.Report(95);

                // Step 5: Report results (100% progress)
                var duration = DateTime.UtcNow - startTime;
                _logger.LogInformation(
                    "Recommendation refresh completed in {Duration:F2} seconds: {Success}/{Total} users successful",
                    duration.TotalSeconds,
                    successfulUsers,
                    users.Count);

                if (failedUsers.Count > 0)
                {
                    _logger.LogWarning("Failed to sync recommendations for {Count} users: {Users}", failedUsers.Count, string.Join(", ", failedUsers));
                }

                progress?.Report(100);
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Recommendation refresh task was cancelled");
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Recommendation refresh task failed");
                throw;
            }
        }

        /// <inheritdoc />
        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            // Daily execution at 4:00 AM by default
            return new[]
            {
                new TaskTriggerInfo
                {
                    Type = MediaBrowser.Model.Tasks.TaskTriggerInfoType.DailyTrigger,
                    TimeOfDayTicks = TimeSpan.FromHours(4).Ticks
                }
            };
        }

        private async Task TriggerAndAwaitLibraryScanAsync(CancellationToken cancellationToken)
        {
            var scanWorker = _taskManager.ScheduledTasks
                .FirstOrDefault(t => string.Equals(t.ScheduledTask.Key, "RefreshLibrary", StringComparison.OrdinalIgnoreCase));

            if (scanWorker == null)
            {
                _logger.LogWarning("Library scan task not found; scan recommendation libraries manually to see updates");
                return;
            }

            _ = _taskManager.Execute(scanWorker, new TaskOptions());
            _logger.LogInformation("Library scan triggered; waiting for completion");

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);

            var deadline = DateTime.UtcNow.AddMinutes(5);
            while (scanWorker.State != TaskState.Idle && DateTime.UtcNow < deadline)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
            }

            if (scanWorker.State == TaskState.Idle)
            {
                _logger.LogInformation("Library scan completed");
            }
            else
            {
                _logger.LogWarning("Library scan did not complete within 5 minutes; proceeding with stale item check");
            }
        }
    }
}
