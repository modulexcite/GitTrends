﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Autofac;
using GitTrends.Shared;
using Shiny;
using Shiny.Notifications;
using Xamarin.Essentials;
using Xamarin.Forms;

namespace GitTrends
{
    public class NotificationService
    {
        const string _trendingRepositoriesNotificationTitle = "Your Repos Are Trending";

        readonly AnalyticsService _analyticsService;

        public NotificationService(AnalyticsService analyticsService) => _analyticsService = analyticsService;

        public ValueTask TrySendTrendingNotificaiton(List<Repository> trendingRepositories, DateTimeOffset? notificationDateTime = null)
        {
#if DEBUG
            return SendTrendingNotification(trendingRepositories, notificationDateTime);
#else
            var repositoriesToNotify = trendingRepositories.Where(shouldSendNotification).ToList();
            return SendTrendingNotification(repositoriesToNotify, notificationDateTime);

            static bool shouldSendNotification(Repository trendingRepository)
            {
                var nextNotificationDate = getMostRecentNotificationDate(trendingRepository).AddDays(3);
                return DateTime.Compare(nextNotificationDate, DateTime.UtcNow) > 1;

                static DateTime getMostRecentNotificationDate(Repository repository) => Preferences.Get(repository.Name, default(DateTime));
            }
#endif
        }

        public async Task HandleReceivedLocalNotification(string title, string message, int badgeCount)
        {
            var baseNavigationPage = await GetBaseNavigationPage();

            if (badgeCount is 1)
            {
                var repositoryName = ParseRepositoryName(message);
                var ownerName = ParseOwnerName(message);

                var shouldNavigateToChart = await MainThread.InvokeOnMainThreadAsync(() =>
                                baseNavigationPage.DisplayAlert($"{repositoryName} is Trending", "Let's check out its chart", "Let's Go", "Not Right Now"));

                _analyticsService.Track("Single Trending Repository Prompt Displayed", "Did Accept Response", shouldNavigateToChart.ToString());

                if (shouldNavigateToChart)
                {
                    //Create repository with only Name & Owner, because those are the only metrics that TrendsPage needs to fetch the chart data
                    var repository = new Repository(repositoryName, string.Empty, 0,
                                                    new RepositoryOwner(ownerName, string.Empty),
                                                    null, string.Empty, new StarGazers(0), false);

                    using var scope = ContainerService.Container.BeginLifetimeScope();
                    var trendsPage = scope.Resolve<TrendsPage>(new TypedParameter(typeof(Repository), repository));

                    await MainThread.InvokeOnMainThreadAsync(async () =>
                    {
                        await baseNavigationPage.PopToRootAsync();
                        await baseNavigationPage.Navigation.PushAsync(trendsPage);
                    });
                }
            }
            else
            {
                _analyticsService.Track("Multiple Trending Repositories Prompt Displayed");

                var messageBuilder = new StringBuilder();

                if (Device.RuntimePlatform is Device.iOS)
                    messageBuilder.AppendLine();

                messageBuilder.AppendLine(message);
                messageBuilder.Append("Sort by Trending to see them all");

                await MainThread.InvokeOnMainThreadAsync(() => baseNavigationPage.DisplayAlert(title, messageBuilder.ToString(), "Thanks!"));
            }
        }

        async ValueTask SendTrendingNotification(List<Repository> trendingRepositories, DateTimeOffset? notificationDateTime)
        {
            if (trendingRepositories.Count is 1)
            {
                var trendingRepository = trendingRepositories.First();

                var notificationService = ShinyHost.Resolve<INotificationManager>();

                var notification = new Notification
                {
                    //iOS crashes when ID is not set
                    Id = Device.RuntimePlatform is Device.iOS ? 1 : 0,
                    Title = _trendingRepositoriesNotificationTitle,
                    Message = CreateSingleRepositoryNotificationMessage(trendingRepository.Name, trendingRepository.OwnerLogin),
                    ScheduleDate = notificationDateTime,
                    BadgeCount = 1
                };

                setMostRecentNotificationDate(trendingRepository);

                await notificationService.Send(notification).ConfigureAwait(false);

                _analyticsService.Track("Single Trending Repository Notification Sent", new Dictionary<string, string>
                {
                    { nameof(Repository.Name), trendingRepository.Name },
                    { nameof(Repository.OwnerLogin), trendingRepository.OwnerLogin },
                });
            }
            else if (trendingRepositories.Count > 1)
            {
                foreach (var repository in trendingRepositories)
                {
                    setMostRecentNotificationDate(repository);
                }

                var notification = new Notification
                {
                    //iOS crashes when ID is not set
                    Id = Device.RuntimePlatform is Device.iOS ? 1 : 0,
                    Title = _trendingRepositoriesNotificationTitle,
                    Message = CreateMultipleRepositoryNotifiationMessage(trendingRepositories.Count),
                    ScheduleDate = notificationDateTime,
                    BadgeCount = trendingRepositories.Count
                };

                var notificationService = ShinyHost.Resolve<INotificationManager>();
                await notificationService.Send(notification).ConfigureAwait(false);

                _analyticsService.Track("Multiple Trending Repositories Notification Sent", getTrendingRepositoriesAnalytics(trendingRepositories));
            }

            static void setMostRecentNotificationDate(Repository repository) => Preferences.Set(repository.Name, DateTime.UtcNow);

            static Dictionary<string, string> getTrendingRepositoriesAnalytics(in List<Repository> trendingRepositories)
            {
                var dictionary = new Dictionary<string, string>
                {
                    { "Count", trendingRepositories.Count.ToString() }
                };

                var owner = trendingRepositories.GroupBy(v => v.OwnerLogin).OrderByDescending(g => g.Count()).First().Key;
                dictionary.Add(nameof(Repository.OwnerLogin), owner);

                foreach (var repository in trendingRepositories)
                {
                    dictionary.Add(nameof(Repository.Name), repository.Name);
                }

                return dictionary;
            }
        }

        string CreateMultipleRepositoryNotifiationMessage(int trendingRepoCount) => $"You have {trendingRepoCount} repos trending";

        string CreateSingleRepositoryNotificationMessage(in string repositoryName, in string repositoryOwner) => $"{repositoryName} by {repositoryOwner} is Trending";
        string ParseRepositoryName(in string? singleRepositoryNotificationMessage) => singleRepositoryNotificationMessage?.Substring(0, singleRepositoryNotificationMessage.IndexOf(" by ")) ?? string.Empty;
        string ParseOwnerName(in string? singleRepositoryNotificationMessage)
        {
            if (string.IsNullOrWhiteSpace(singleRepositoryNotificationMessage))
                return string.Empty;

            var ownerNameIndex = singleRepositoryNotificationMessage.IndexOf(" by ") + " by ".Length;
            var ownerNameLength = singleRepositoryNotificationMessage.IndexOf(" is Trending", ownerNameIndex) - ownerNameIndex;
            return singleRepositoryNotificationMessage.Substring(ownerNameIndex, ownerNameLength);
        }

        async ValueTask<BaseNavigationPage> GetBaseNavigationPage()
        {
            if (Application.Current.MainPage is BaseNavigationPage baseNavigationPage)
                return baseNavigationPage;

            var tcs = new TaskCompletionSource<BaseNavigationPage>();

            Application.Current.PageAppearing += HandlePageAppearing;

            return await tcs.Task.ConfigureAwait(false);

            void HandlePageAppearing(object sender, Page page)
            {
                if (page is BaseNavigationPage baseNavigationPage)
                {
                    Application.Current.PageAppearing -= HandlePageAppearing;
                    tcs.SetResult(baseNavigationPage);
                }
                else if (page.Parent is BaseNavigationPage baseNavigation)
                {
                    Application.Current.PageAppearing -= HandlePageAppearing;
                    tcs.SetResult(baseNavigation);
                }
            }
        }
    }
}