using Microsoft.Extensions.DependencyInjection;
using Shiny.AppDeviceBridge.Client;
using Shiny.AppDeviceBridge.Notifications;
using Shiny.AppDeviceBridge.Notifications.Client;
using Shiny.Notifications;
using Notification = Shiny.Notifications.Notification;
using ChannelActionType = Shiny.AppDeviceBridge.Notifications.Client.ChannelActionType;

namespace Shiny.AppDeviceBridge.Tests;

/// <summary>Requesting notification access over a fake <see cref="INotificationManager"/>.</summary>
public class NotificationsBridgeTests
{
    [Fact]
    public async Task Answers_the_platforms_decision()
    {
        await using var fixture = await NotificationsFixture.StartAsync(new FakeNotificationManager { Answer = Shiny.AccessState.Available });

        var result = await fixture.Client.RequestAccessAsync(new NotificationAccessRequest());

        Assert.Equal(Client.AccessState.Available, result.Access);
    }

    [Fact]
    public async Task A_request_the_platform_fails_after_the_user_turned_notifications_off_answers_denied()
    {
        // macOS: UNErrorDomain Code=1 "Notifications are not allowed for this application".
        await using var fixture = await NotificationsFixture.StartAsync(new FakeNotificationManager
        {
            Current = Shiny.AccessState.Denied,
            Failure = new Exception("Notifications are not allowed for this application")
        });

        var result = await fixture.Client.RequestAccessAsync(new NotificationAccessRequest());

        Assert.Equal(Client.AccessState.Denied, result.Access);
    }

    [Fact]
    public async Task A_request_that_fails_for_another_reason_is_still_a_failure()
    {
        await using var fixture = await NotificationsFixture.StartAsync(new FakeNotificationManager
        {
            Current = Shiny.AccessState.Unknown,
            Failure = new Exception("boom")
        });

        var failed = await Assert.ThrowsAsync<BridgeException>(() => fixture.Client.RequestAccessAsync(new NotificationAccessRequest()));

        Assert.Equal("bridge_failed", failed.Code);
    }

    [Fact]
    public async Task Channel_actions_carry_requires_unlock_both_ways()
    {
        var manager = new FakeNotificationManager();
        await using var fixture = await NotificationsFixture.StartAsync(manager);

        var added = await fixture.Client.AddChannelAsync(new NotificationChannel(
            "garage",
            Actions:
            [
                new NotificationChannelAction("open", "Open Garage", ChannelActionType.OpenApp, RequiresUnlock: true),
                new NotificationChannelAction("snooze", "Snooze")
            ]
        ));

        var native = Assert.Single(manager.Channels).Actions;
        Assert.True(native[0].RequiresUnlock);
        Assert.Equal(Shiny.Notifications.ChannelActionType.OpenApp, native[0].ActionType);
        Assert.False(native[1].RequiresUnlock);

        Assert.Equal(
            [
                new NotificationChannelAction("open", "Open Garage", ChannelActionType.OpenApp, true),
                new NotificationChannelAction("snooze", "Snooze", ChannelActionType.None, false)
            ],
            added.Actions
        );
        Assert.True(Assert.Single(await fixture.Client.GetChannelsAsync()).Actions![0].RequiresUnlock);
    }

    sealed class NotificationsFixture : IAsyncDisposable
    {
        BuiltInClientTests.HostFixture host = null!;

        public NotificationsBridgeClient Client { get; private set; } = null!;

        public static async Task<NotificationsFixture> StartAsync(INotificationManager manager)
        {
            var fixture = new NotificationsFixture();

            var services = new ServiceCollection();
            services.AddSingleton(manager);
            services.AddSingleton<IWebAppMainThread, InlineMainThread>();
            var provider = services.BuildServiceProvider();

            fixture.host = await BuiltInClientTests.HostFixture.StartAsync(
                app => [new NotificationsBridge(provider, new WebAppFileRoots(app.BridgeOptions()))],
                null,
                _ => { }
            );

            fixture.Client = new NotificationsBridgeClient(fixture.host.Transport);
            return fixture;
        }

        public ValueTask DisposeAsync() => this.host.DisposeAsync();
    }

    sealed class InlineMainThread : IWebAppMainThread
    {
        public Task<T> InvokeAsync<T>(Func<Task<T>> action) => action();
    }

    sealed class FakeNotificationManager : INotificationManager
    {
        public Shiny.AccessState Answer { get; init; }
        public Shiny.AccessState Current { get; init; }
        public Exception? Failure { get; init; }

        public Task<Shiny.AccessState> RequestAccess(AccessRequestFlags flags = AccessRequestFlags.Notification)
            => this.Failure is { } failure ? Task.FromException<Shiny.AccessState>(failure) : Task.FromResult(this.Answer);

        public Task<Shiny.AccessState> GetCurrentAccess(AccessRequestFlags flags = AccessRequestFlags.Notification) => Task.FromResult(this.Current);

        public List<Channel> Channels { get; } = [];

        public void AddChannel(Channel channel) => this.Channels.Add(channel);
        public void RemoveChannel(string channelId) => this.Channels.RemoveAll(x => x.Identifier == channelId);
        public void ClearChannels() => this.Channels.Clear();
        public Channel? GetChannel(string channelId) => this.Channels.FirstOrDefault(x => x.Identifier == channelId);
        public IList<Channel> GetChannels() => this.Channels;
        public Task<Notification>? GetNotification(int notificationId) => null;
        public Task Cancel(int id) => Task.CompletedTask;
        public Task Cancel(CancelScope cancelScope = CancelScope.All) => Task.CompletedTask;
        public Task<IList<Notification>> GetPendingNotifications() => Task.FromResult<IList<Notification>>([]);
        public Task Send(Notification notification) => Task.CompletedTask;
    }
}
