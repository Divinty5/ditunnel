using Android.App;
using Android.Appwidget;
using Android.Content;
using Android.Graphics;
using Android.OS;
using Android.Views;
using Android.Widget;
using DiTunnel.App;
using DiTunnel.Core.Profiles;

namespace DiTunnel.Android;

internal static class VpnWidgets
{
    private static readonly Type[] Providers = [typeof(VpnWidgetSmall), typeof(VpnWidgetMedium), typeof(VpnWidget43), typeof(VpnWidget44), typeof(VpnWidget45)];
    public static void Refresh(Context context)
    {
        var manager = AppWidgetManager.GetInstance(context);
        if (manager is null) return;
        foreach (var type in Providers)
        {
            var ids = manager.GetAppWidgetIds(new ComponentName(context, Java.Lang.Class.FromType(type))) ?? [];
            foreach (var id in ids) Update(context, manager, id, type);
        }
    }
    public static void Update(Context context, AppWidgetManager manager, int id, Type provider)
    {
        var small = provider == typeof(VpnWidgetSmall);
        var large = provider != typeof(VpnWidgetSmall) && provider != typeof(VpnWidgetMedium);
        var views = new RemoteViews(context.PackageName, small ? Resource.Layout.widget_small : large ? Resource.Layout.widget_large : Resource.Layout.widget_medium);
        var selected = VpnQuickControls.Selected;
        var status = VpnQuickControls.StatusText;
        var power = VpnQuickControls.Active ? L.T("Отключить") : L.T("Подключить");
        using var bitmap = PowerBitmap(VpnQuickControls.Active, VpnQuickControls.Busy);
        views.SetImageViewBitmap(Resource.Id.widget_power, bitmap);
        views.SetBoolean(Resource.Id.widget_power, "setEnabled", VpnQuickControls.CanToggle);
        views.SetContentDescription(Resource.Id.widget_power, $"Di-Tunnel · {power} · {selected?.Name}");
        PendingIntent toggle;
        if (VpnQuickControls.NeedsActivity) toggle = VpnQuickControls.ActivityPendingIntent();
        else toggle = VpnControlService.Pending(context, 1702, VpnQuickControls.ToggleAction,
            PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);
        views.SetOnClickPendingIntent(Resource.Id.widget_power, toggle);
        if (!small)
        {
            views.SetTextViewText(Resource.Id.widget_name, selected?.Name ?? L.T("Сначала выберите сервер."));
            views.SetTextViewText(Resource.Id.widget_status, VpnQuickControls.Status.State == DiTunnel.Core.Connection.VpnConnectionState.Error
                ? L.T(VpnQuickControls.Status.Message ?? "Не удалось подключиться") : status);
            views.SetOnClickPendingIntent(Resource.Id.widget_name, VpnQuickControls.ActivityPendingIntent("android.intent.action.MAIN"));
        }
        if (large)
        {
            views.SetBoolean(Resource.Id.widget_servers, "setEnabled", !VpnQuickControls.Busy);
            views.SetTextViewText(Resource.Id.widget_heading, "Di-Tunnel · " + (selected?.SourceName ?? L.T("Серверы подписки")));
            views.SetTextViewText(Resource.Id.widget_empty, L.T("Сначала выберите сервер."));
            var adapter = new Intent(context, typeof(VpnWidgetServersService)).PutExtra(AppWidgetManager.ExtraAppwidgetId, id);
            adapter.SetData(global::Android.Net.Uri.Parse($"ditunnel-widget://servers/{id}"));
#pragma warning disable CA1422
            views.SetRemoteAdapter(Resource.Id.widget_servers, adapter);
            views.SetEmptyView(Resource.Id.widget_servers, Resource.Id.widget_empty);
            var flags = PendingIntentFlags.UpdateCurrent | (OperatingSystem.IsAndroidVersionAtLeast(31) ? PendingIntentFlags.Mutable : 0);
            var template = VpnControlService.Pending(context, 1703, VpnQuickControls.SelectAction, flags);
            views.SetPendingIntentTemplate(Resource.Id.widget_servers, template);
#pragma warning restore CA1422
        }
        manager.UpdateAppWidget(id, views);
        if (large)
        {
#pragma warning disable CA1422
            manager.NotifyAppWidgetViewDataChanged([id], Resource.Id.widget_servers);
#pragma warning restore CA1422
        }
    }
    private static Bitmap PowerBitmap(bool active, bool busy)
    {
        var bitmap = Bitmap.CreateBitmap(240, 240, Bitmap.Config.Argb8888!)!;
        using var canvas = new Canvas(bitmap);
        using var paint = new Paint(PaintFlags.AntiAlias);
        var color = Color.ParseColor(busy ? "#C3A9ED" : active ? "#5DFF95" : "#FF6D85");
        paint.Color = color; paint.Alpha = 35; paint.SetStyle(Paint.Style.Stroke); paint.StrokeWidth = 20;
        canvas.DrawCircle(120, 120, 102, paint);
        paint.Color = Color.ParseColor("#262037"); paint.Alpha = 255; paint.SetStyle(Paint.Style.Fill);
        canvas.DrawCircle(120, 120, 98, paint);
        paint.Color = color; paint.SetStyle(Paint.Style.Stroke); paint.StrokeWidth = 3;
        canvas.DrawCircle(120, 120, 98, paint);
        paint.StrokeWidth = 12; paint.StrokeCap = Paint.Cap.Round;
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
        private IReadOnlyList<ImportedProfile> servers = [];
        public int Count => servers.Count;
        public bool HasStableIds => true;
        public RemoteViews? LoadingView => null;
        public int ViewTypeCount => 1;
        public void OnCreate() => OnDataSetChanged();
        public void OnDestroy() { servers = []; }
        public void OnDataSetChanged() => servers = VpnQuickControls.Servers;
        public long GetItemId(int position) => position >= 0 && position < servers.Count
            ? unchecked((long)Convert.ToUInt64(VpnQuickControls.Key(servers[position])[..16], 16)) : -1;
        public RemoteViews GetViewAt(int position)
        {
            var row = new RemoteViews(context.PackageName, Resource.Layout.widget_server_row);
            if (position < 0 || position >= servers.Count) return row;
            var profile = servers[position];
            var selected = VpnQuickControls.Selected;
            row.SetTextViewText(Resource.Id.widget_server_name, (selected is not null && VpnQuickControls.Key(profile) == VpnQuickControls.Key(selected) ? "● " : "○ ") + profile.Name);
            row.SetTextViewText(Resource.Id.widget_server_protocol, profile.ProtocolName);
            row.SetOnClickFillInIntent(Resource.Id.widget_server_row, new Intent().PutExtra(VpnQuickControls.ProfileExtra, VpnQuickControls.Key(profile)));
            return row;
        }
    }
}
