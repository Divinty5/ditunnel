using Android.App;
using Android.Appwidget;
using Android.Content;
using Android.Graphics;
using Android.OS;
using Android.Views;
using Android.Widget;
using DiTunnel.App;
using DiTunnel.App.ViewModels;
using DiTunnel.Core.Connection;
using DiTunnel.Core.Profiles;

namespace DiTunnel.Android;

internal static class VpnWidgets
{
    internal static bool HasWidgets(Context context)
    {
        var manager = AppWidgetManager.GetInstance(context);
        return manager is not null && Providers.Any(type =>
            manager.GetAppWidgetIds(new ComponentName(context, Java.Lang.Class.FromType(type))) is { Length: > 0 });
    }
    internal static bool IsLight(Context context) => UserSettings.Current.Theme == "light" ||
        UserSettings.Current.Theme == "system" &&
        (context.Resources?.Configuration?.UiMode & global::Android.Content.Res.UiMode.NightMask) == global::Android.Content.Res.UiMode.NightNo;
    internal static Color TextColor(bool light) => Color.ParseColor(light ? "#241B35" : "#EDE6FF");
    internal static Color AccentColor(bool light) => Color.ParseColor(light ? "#644096" : "#C3A9ED");
    private static readonly Type[] Providers = [typeof(VpnWidgetSmall), typeof(VpnWidgetMedium), typeof(VpnWidget43), typeof(VpnWidget44), typeof(VpnWidget45)];
    // Provider callbacks and the refresh queue both run on the Android main looper.
    private static readonly HashSet<int> InitializedWidgets = [];
    private static IReadOnlyList<ImportedProfile>? lastProfiles;
    private static ImportedProfile? lastDisplayed;
    private static bool lastLight;
    private sealed record WidgetSnapshot(ImportedProfile? Profile, VpnQuickControls.Presentation Presentation,
        string Status, bool Light, bool CanToggle, bool Busy, bool Active, bool NeedsActivity);

    private static WidgetSnapshot Capture(Context context)
    {
        VpnQuickControls.EnsureLatencyMonitoring();
        var current = VpnQuickControls.Status;
        var running = VpnQuickControls.ActiveProfile;
        var exhausted = VpnQuickControls.IsSubscriptionExhausted(running);
        var presentation = VpnQuickControls.GetPresentation(current, running, exhausted);
        var status = presentation is { State: VpnConnectionState.Connected, DisplayState: VpnConnectionState.Connecting }
            ? L.T("Идёт проверка…") : current.State == VpnConnectionState.Error
                ? L.T(current.Message ?? "Не удалось подключиться") : VpnQuickControls.StatusTextFor(current.State, exhausted);
        var transition = current.State is VpnConnectionState.Connecting or VpnConnectionState.Disconnecting;
        return new(VpnQuickControls.WidgetProfile(current.State, running), presentation, status, IsLight(context), !transition,
            transition || current.State == VpnConnectionState.Reconnecting,
            current.State is VpnConnectionState.Connected or VpnConnectionState.Reconnecting, VpnQuickControls.NeedsActivity);
    }

    public static void Refresh(Context context)
    {
        var manager = AppWidgetManager.GetInstance(context);
        if (manager is null) return;
        var widgets = Providers.Select(type => (Type: type,
            Ids: manager.GetAppWidgetIds(new ComponentName(context, Java.Lang.Class.FromType(type))) ?? [])).ToArray();
        InitializedWidgets.IntersectWith(widgets.SelectMany(widget => widget.Ids));
        if (widgets.All(widget => widget.Ids.Length == 0)) return;
        var snapshot = Capture(context);
        using var bitmap = PowerBitmap(snapshot.Presentation.DisplayState, snapshot.Light);
        var profiles = VpnQuickControls.Profiles();
        var refreshList = !ReferenceEquals(profiles, lastProfiles) || snapshot.Profile != lastDisplayed || snapshot.Light != lastLight;

        foreach (var (type, ids) in widgets)
        {
            var initialized = ids.Where(InitializedWidgets.Contains).ToArray();
            if (initialized.Length > 0)
            {
                using var views = CreateViews(context, type, snapshot, bitmap);
                manager.PartiallyUpdateAppWidget(initialized, views);
            }
            foreach (var id in ids.Where(id => !InitializedWidgets.Contains(id))) Update(context, manager, id, type, snapshot, bitmap);
        }
#pragma warning disable CA1422
        if (refreshList)
            foreach (var (type, ids) in widgets)
                if (IsLarge(type) && ids.Length > 0) manager.NotifyAppWidgetViewDataChanged(ids, Resource.Id.widget_servers);
#pragma warning restore CA1422
        lastProfiles = profiles;
        lastDisplayed = snapshot.Profile;
        lastLight = snapshot.Light;
    }

    public static void Update(Context context, AppWidgetManager manager, int id, Type provider)
    {
        var snapshot = Capture(context);
        using var bitmap = PowerBitmap(snapshot.Presentation.DisplayState, snapshot.Light);
        Update(context, manager, id, provider, snapshot, bitmap);
    }

    private static bool IsLarge(Type provider) => provider != typeof(VpnWidgetSmall) && provider != typeof(VpnWidgetMedium);

    private static RemoteViews CreateViews(Context context, Type provider, WidgetSnapshot snapshot, Bitmap bitmap, int? newWidgetId = null)
    {
        var small = provider == typeof(VpnWidgetSmall);
        var large = IsLarge(provider);
        var views = new RemoteViews(context.PackageName, small ? Resource.Layout.widget_small : large ? Resource.Layout.widget_large : Resource.Layout.widget_medium);
        var (profile, presentation, status, light, canToggle, busy, active, needsActivity) = snapshot;
        var name = profile?.DisplayName ?? (active ? "—" : L.T("Сначала выберите сервер."));
        var power = L.T(active ? "Отключить" : "Подключить");
        views.SetImageViewBitmap(Resource.Id.widget_power, bitmap);
        views.SetBoolean(Resource.Id.widget_power, "setEnabled", canToggle);
        views.SetContentDescription(Resource.Id.widget_power, $"Di-Tunnel · {power} · {name}");
        PendingIntent toggle;
        if (needsActivity) toggle = VpnQuickControls.ActivityPendingIntent();
        else toggle = VpnControlService.Pending(context, 1702, VpnQuickControls.ToggleAction,
            PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);
        views.SetOnClickPendingIntent(Resource.Id.widget_power, toggle);
        if (!small)
        {
            views.SetInt(Resource.Id.widget_root, "setBackgroundResource", light ? Resource.Drawable.widget_background_light : Resource.Drawable.widget_background);
            views.SetTextColor(Resource.Id.widget_name, TextColor(light));
            views.SetTextColor(Resource.Id.widget_status, NativeColor(ConnectionPalette.ForegroundFor(presentation.DisplayState, light)));
            views.SetTextViewText(Resource.Id.widget_name, name);
            views.SetTextViewText(Resource.Id.widget_status, status);
            views.SetOnClickPendingIntent(Resource.Id.widget_name, VpnQuickControls.ActivityPendingIntent("android.intent.action.MAIN"));
        }
        if (large)
        {
            var delay = presentation.Delay;
            views.SetViewVisibility(Resource.Id.widget_latency, delay is null ? ViewStates.Gone : ViewStates.Visible);
            views.SetTextViewText(Resource.Id.widget_latency, delay is { } milliseconds ? L.T($"≈  {milliseconds:0} мс") : "");
            views.SetInt(Resource.Id.widget_latency, "setBackgroundResource", light ? Resource.Drawable.widget_latency_light : Resource.Drawable.widget_latency_dark);
            var brush = DiTunnel.App.ViewModels.LatencyPalette.ForTheme(DiTunnel.App.ViewModels.LatencyPalette.For(delay), light);
            var color = ((global::Avalonia.Media.ISolidColorBrush)brush).Color;
            views.SetTextColor(Resource.Id.widget_latency, new Color(color.R, color.G, color.B));
            views.SetTextColor(Resource.Id.widget_heading, AccentColor(light));
            views.SetTextColor(Resource.Id.widget_empty, AccentColor(light));
            views.SetBoolean(Resource.Id.widget_servers, "setEnabled", !busy);
            views.SetTextViewText(Resource.Id.widget_heading, "Di-Tunnel · " + (profile?.SourceName ?? L.T("Серверы подписки")));
            views.SetTextViewText(Resource.Id.widget_empty, active ? "" : L.T("Сначала выберите сервер."));
        }
        if (large && newWidgetId is { } id)
        {
            using var adapter = new Intent(context, typeof(VpnWidgetServersService)).PutExtra(AppWidgetManager.ExtraAppwidgetId, id);
            adapter.SetData(global::Android.Net.Uri.Parse($"ditunnel-widget://servers/{id}"));
#pragma warning disable CA1422
            views.SetRemoteAdapter(Resource.Id.widget_servers, adapter);
            views.SetEmptyView(Resource.Id.widget_servers, Resource.Id.widget_empty);
            var flags = PendingIntentFlags.UpdateCurrent | (OperatingSystem.IsAndroidVersionAtLeast(31) ? PendingIntentFlags.Mutable : 0);
            var template = VpnControlService.Pending(context, 1703, VpnQuickControls.SelectAction, flags);
            views.SetPendingIntentTemplate(Resource.Id.widget_servers, template);
#pragma warning restore CA1422
        }
        return views;
    }

    private static void Update(Context context, AppWidgetManager manager, int id, Type provider, WidgetSnapshot snapshot, Bitmap bitmap)
    {
        using var views = CreateViews(context, provider, snapshot, bitmap, id);
        manager.UpdateAppWidget(id, views);
        InitializedWidgets.Add(id);
    }
    private static Color NativeColor(global::Avalonia.Media.Color color) => new(color.R, color.G, color.B);

    private static Bitmap PowerBitmap(VpnConnectionState state, bool light)
    {
        var bitmap = Bitmap.CreateBitmap(240, 240, Bitmap.Config.Argb8888!)!;
        using var canvas = new Canvas(bitmap);
        using var paint = new Paint(PaintFlags.AntiAlias);
        var border = NativeColor(ConnectionPalette.AccentFor(state));
        var foreground = NativeColor(ConnectionPalette.ForegroundFor(state, light));
        if (state != VpnConnectionState.Disconnected)
        {
            paint.Color = border; paint.Alpha = 35; paint.SetStyle(Paint.Style.Stroke); paint.StrokeWidth = 20;
            canvas.DrawCircle(120, 120, 102, paint);
        }
        paint.Color = Color.ParseColor(light ? "#E9DFFA" : "#262037"); paint.Alpha = 255; paint.SetStyle(Paint.Style.Fill);
        canvas.DrawCircle(120, 120, 98, paint);
        paint.Color = border; paint.SetStyle(Paint.Style.Stroke); paint.StrokeWidth = 3;
        canvas.DrawCircle(120, 120, 98, paint);
        paint.Color = foreground; paint.StrokeWidth = 12; paint.StrokeCap = Paint.Cap.Round;
        using var arc = new RectF(83, 83, 157, 157);
        canvas.DrawArc(arc, -45, 270, false, paint);
        canvas.DrawLine(120, 72, 120, 119, paint);
        return bitmap;
    }
}

public abstract class VpnWidgetProvider : AppWidgetProvider
{
    public override void OnUpdate(Context? context, AppWidgetManager? manager, int[]? ids)
    {
        if (context is null || manager is null) return;
        foreach (var id in ids ?? []) VpnWidgets.Update(context, manager, id, GetType());
        VpnControlsRefreshScheduler.Update(context);
    }
    public override void OnDisabled(Context? context)
    {
        base.OnDisabled(context);
        if (context is not null) VpnControlsRefreshScheduler.Update(context);
    }
    public override void OnAppWidgetOptionsChanged(Context? context, AppWidgetManager? manager, int id, Bundle? options)
    {
        if (context is not null && manager is not null) VpnWidgets.Update(context, manager, id, GetType());
    }
}
[BroadcastReceiver(Name = "com.divintyinteractive.ditunnel.VpnWidgetSmall", Label = "Di-Tunnel · 1×1", Exported = true)]
[IntentFilter([AppWidgetManager.ActionAppwidgetUpdate])]
[MetaData("android.appwidget.provider", Resource = "@xml/widget_small")]
public sealed class VpnWidgetSmall : VpnWidgetProvider { }
[BroadcastReceiver(Name = "com.divintyinteractive.ditunnel.VpnWidgetMedium", Label = "Di-Tunnel · 2×2", Exported = true)]
[IntentFilter([AppWidgetManager.ActionAppwidgetUpdate])]
[MetaData("android.appwidget.provider", Resource = "@xml/widget_medium")]
public sealed class VpnWidgetMedium : VpnWidgetProvider { }
[BroadcastReceiver(Name = "com.divintyinteractive.ditunnel.VpnWidget43", Label = "Di-Tunnel · 4×3", Exported = true)]
[IntentFilter([AppWidgetManager.ActionAppwidgetUpdate])]
[MetaData("android.appwidget.provider", Resource = "@xml/widget_43")]
public sealed class VpnWidget43 : VpnWidgetProvider { }
[BroadcastReceiver(Name = "com.divintyinteractive.ditunnel.VpnWidget44", Label = "Di-Tunnel · 4×4", Exported = true)]
[IntentFilter([AppWidgetManager.ActionAppwidgetUpdate])]
[MetaData("android.appwidget.provider", Resource = "@xml/widget_44")]
public sealed class VpnWidget44 : VpnWidgetProvider { }
[BroadcastReceiver(Name = "com.divintyinteractive.ditunnel.VpnWidget45", Label = "Di-Tunnel · 4×5", Exported = true)]
[IntentFilter([AppWidgetManager.ActionAppwidgetUpdate])]
[MetaData("android.appwidget.provider", Resource = "@xml/widget_45")]
public sealed class VpnWidget45 : VpnWidgetProvider { }

[Service(Name = "com.divintyinteractive.ditunnel.VpnWidgetServersService", Exported = false, Permission = "android.permission.BIND_REMOTEVIEWS")]
public sealed class VpnWidgetServersService : RemoteViewsService
{
    public override IRemoteViewsFactory OnGetViewFactory(Intent? intent) => new ServersFactory(this);
    private sealed class ServersFactory(Context context) : Java.Lang.Object, IRemoteViewsFactory
    {
        private sealed record ServerList(ImportedProfile? Displayed, ImportedProfile[] Servers);
        private ServerList data = new(null, []);
        public int Count => Volatile.Read(ref data).Servers.Length;
        public bool HasStableIds => true;
        public RemoteViews? LoadingView => null;
        public int ViewTypeCount => 1;
        public void OnCreate() => OnDataSetChanged();
        public void OnDestroy() => Volatile.Write(ref data, new(null, []));
        public void OnDataSetChanged()
        {
            var profile = VpnQuickControls.WidgetProfile(VpnQuickControls.Status.State, VpnQuickControls.ActiveProfile);
            Volatile.Write(ref data, new(profile, VpnWidgetProfiles.Servers(profile, VpnQuickControls.Profiles())));
        }
        public long GetItemId(int position)
        {
            var servers = Volatile.Read(ref data).Servers;
            return position >= 0 && position < servers.Length
                ? unchecked((long)Convert.ToUInt64(VpnQuickControls.Key(servers[position])[..16], 16)) : -1;
        }
        public RemoteViews GetViewAt(int position)
        {
            var row = new RemoteViews(context.PackageName, Resource.Layout.widget_server_row);
            var light = VpnWidgets.IsLight(context);
            row.SetTextColor(Resource.Id.widget_server_name, VpnWidgets.TextColor(light));
            row.SetTextColor(Resource.Id.widget_server_protocol, VpnWidgets.AccentColor(light));
            var (displayed, servers) = Volatile.Read(ref data);
            if (position < 0 || position >= servers.Length) return row;
            var profile = servers[position];
            row.SetTextViewText(Resource.Id.widget_server_name, (VpnWidgetProfiles.SameProfile(profile, displayed) ? "● " : "○ ") + profile.DisplayName);
            row.SetTextViewText(Resource.Id.widget_server_protocol, profile.ProtocolName);
            row.SetOnClickFillInIntent(Resource.Id.widget_server_row, new Intent().PutExtra(VpnQuickControls.ProfileExtra, VpnQuickControls.Key(profile)));
            return row;
        }
    }
}
