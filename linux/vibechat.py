#!/usr/bin/python3
# VibeChat for Linux - tiny GTK desktop client for WhatsApp Web, using the system WebKitGTK engine.
# The Linux counterpart of VibeChat.cs. Not affiliated with WhatsApp or Meta.
# Needs: python3-gi, gir1.2-gtk-3.0, gir1.2-webkit2-4.1, gir1.2-ayatanaappindicator3-0.1 (tray, optional)
import importlib
import io
import json
import os
import re
import shlex
import shutil
import signal
import sys
import tarfile
import threading
import time
import urllib.error
import urllib.parse
import urllib.request

APP_ID = "io.github.madvibecoder.VibeChat"
APP_NAME = "VibeChat"
VERSION = "@VERSION@"  # filled in by build-deb.py
APP_HOST = "web.whatsapp.com"
START_URL = "https://" + APP_HOST + "/"
# WhatsApp Web supports Safari, so present WebKitGTK (the same engine) as Safari on Linux. Tested: WhatsApp accepts
# it. Being honest about Linux keeps keyboard shortcuts on Ctrl rather than the Mac Command key.
USER_AGENT = ("Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/605.1.15 "
              "(KHTML, like Gecko) Version/18.0 Safari/605.1.15")
ICON = APP_ID
ICON_UNREAD = APP_ID + "-unread"
MAX_WEB_RESTARTS = 5
RETRY_SECONDS = 5
# Updates come from this repository's GitHub releases.
RELEASES_URL = "https://github.com/MadVibeCoder/vibechat/releases"
DEB_ASSET = "VibeChat.deb"
FIRST_UPDATE_CHECK = 120              # seconds after start, so startup settles first
UPDATE_CHECK_INTERVAL = 12 * 60 * 60
BACKGROUND = (0x11 / 255, 0x1B / 255, 0x21 / 255)  # WhatsApp Web's dark background: no white flash while loading
OFFLINE_HTML = """<!doctype html><meta charset="utf-8"><body style="margin:0;height:100vh;display:flex;align-items:center;
justify-content:center;background:#111b21;color:#aebac1;font:15px system-ui,sans-serif">
<p>Can't reach WhatsApp Web. Retrying&hellip;</p>"""


# ---------- pure helpers (no GTK; unit-tested) ----------
def is_app_origin(uri):
    """Only WhatsApp Web itself is granted notifications, microphone, camera, etc."""
    try:
        u = urllib.parse.urlsplit(uri or "")
        return u.scheme == "https" and (u.hostname or "").lower() == APP_HOST and u.port in (None, 443)
    except ValueError:
        return False


def is_app_page(uri):
    """Pages allowed inside the window: WhatsApp Web, its blob: URLs and about:blank. Links elsewhere open in the browser."""
    uri = uri or ""
    if uri.startswith("about:"):
        return True
    if uri.lower().startswith("blob:" + START_URL):
        return True
    return is_app_origin(uri)


def is_external_scheme(uri):
    return urllib.parse.urlsplit(uri or "").scheme.lower() in ("http", "https", "mailto", "tel")


def unread_count(title):
    """WhatsApp Web titles look like "(3) WhatsApp". Python's int() also reads non-ASCII digits."""
    m = re.match(r"\((\d+)\)", title or "")
    if not m:
        return 0
    try:
        return min(int(m.group(1)), 99999)
    except ValueError:
        return 0


def unique_path(folder, suggested):
    """A free file name in folder for a download; never lets the page choose the directory."""
    name = os.path.basename((suggested or "").replace("\\", "/")).strip() or "download"
    base, ext = os.path.splitext(name)
    path, i = os.path.join(folder, name), 1
    while os.path.exists(path):
        path = os.path.join(folder, "%s (%d)%s" % (base, i, ext))
        i += 1
    return path


def desktop_exec_quote(arg):
    """Quote one argument for a .desktop Exec= line."""
    if re.fullmatch(r"[A-Za-z0-9_/.@%+=:,-]+", arg):
        return arg
    return '"' + re.sub(r'(["`$\\])', r"\\\1", arg) + '"'


def parse_version(text):
    """'v1.4.0' or '1.4.0' -> (1, 4, 0); anything else (e.g. an unbuilt '@VERSION@') -> None."""
    m = re.fullmatch(r"v?(\d+(?:\.\d+){1,3})", (text or "").strip())
    return tuple(int(p) for p in m.group(1).split(".")) if m else None


def format_version(version):
    return ".".join(str(p) for p in version)


def release_version_from_url(url):
    """GitHub sends /releases/latest on to /releases/tag/v1.4.0; read the version from there (no API needed)."""
    m = re.search(r"/releases/tag/(v?\d+(?:\.\d+){1,3})/?$", url or "")
    return parse_version(m.group(1)) if m else None


def deb_control(path):
    """The fields of a .deb's control file (Package, Version, ...), read without dpkg."""
    with open(path, "rb") as f:
        data = f.read()
    if data[:8] != b"!<arch>\n":
        raise ValueError("not a .deb package")
    pos = 8
    while pos + 60 <= len(data):
        name = data[pos:pos + 16].decode("ascii", "replace").strip().rstrip("/")
        size = int(data[pos + 48:pos + 58])
        body = data[pos + 60:pos + 60 + size]
        pos += 60 + size + (size & 1)
        if name.startswith("control.tar"):
            with tarfile.open(fileobj=io.BytesIO(body)) as tar:
                for member in tar.getmembers():
                    if member.name.lstrip("./") == "control":
                        text = tar.extractfile(member).read().decode("utf-8", "replace")
                        return dict(re.findall(r"^([A-Za-z0-9-]+):[ \t]*(.*?)\s*$", text, re.M))
    raise ValueError("no control file in the package")


class _KeepRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, *args, **kwargs):
        return None  # don't follow: the redirect target itself is the answer


def fetch_latest_version():
    """Ask where /releases/latest redirects to, without fetching that page."""
    request = urllib.request.Request(RELEASES_URL + "/latest", method="HEAD",
                                     headers={"User-Agent": "VibeChat/%s (Linux)" % VERSION})
    try:
        with urllib.request.build_opener(_KeepRedirect).open(request, timeout=30):
            return None  # no redirect: there is no release yet
    except urllib.error.HTTPError as e:
        if e.code not in (301, 302, 303, 307, 308):
            raise
        return release_version_from_url(urllib.parse.urljoin(RELEASES_URL, e.headers.get("Location", "")))


def download_update(version, folder):
    """Download the .deb for version and make sure it really is VibeChat at exactly that version before trusting it."""
    v = format_version(version)
    shutil.rmtree(folder, ignore_errors=True)
    os.makedirs(folder)
    part = os.path.join(folder, "download.part")
    url = "%s/download/v%s/%s" % (RELEASES_URL, v, DEB_ASSET)
    request = urllib.request.Request(url, headers={"User-Agent": "VibeChat/%s (Linux)" % VERSION})
    with urllib.request.urlopen(request, timeout=60) as response, open(part, "wb") as out:
        if urllib.parse.urlsplit(response.geturl()).scheme != urllib.parse.urlsplit(url).scheme:
            raise ValueError("the download was redirected to an insecure address")
        total = 0
        while True:
            chunk = response.read(65536)
            if not chunk:
                break
            total += len(chunk)
            if total > 64 * 1024 * 1024:
                raise ValueError("the download is unexpectedly large")
            out.write(chunk)
    try:
        fields = deb_control(part)
        if fields.get("Package") != "vibechat" or fields.get("Version") != v:
            raise ValueError("the download is not the VibeChat %s package" % v)
    except (ValueError, OSError, tarfile.TarError):
        os.remove(part)  # never keep a file we refused
        raise
    deb = os.path.join(folder, "vibechat_%s_all.deb" % v)
    os.replace(part, deb)
    return deb


def spell_languages(names):
    """GLib language names (e.g. ['en_CA.UTF-8', 'en_CA', 'en', 'C']) -> spell-checker codes."""
    langs = [n for n in names if re.fullmatch(r"[a-z]{2,3}_[A-Z]{2}", n)]
    return langs[:1] or ["en_US"]


# ---------- GTK app ----------
# WebKitGTK's DMA-BUF renderer shows a blank window with some GPU drivers (notably NVIDIA, and in VMs);
# the shared-memory path is plenty fast for a chat app. Must be set before WebKit loads.
os.environ.setdefault("WEBKIT_DISABLE_DMABUF_RENDERER", "1")

import gi  # noqa: E402

gi.require_version("Gtk", "3.0")
gi.require_version("Gdk", "3.0")
try:
    gi.require_version("WebKit2", "4.1")
except ValueError:
    gi.require_version("WebKit2", "4.0")
from gi.repository import Gdk, Gio, GLib, Gtk, WebKit2  # noqa: E402

AppIndicator = None
for _name in ("AyatanaAppIndicator3", "AppIndicator3"):
    try:
        gi.require_version(_name, "0.1")
        AppIndicator = importlib.import_module("gi.repository." + _name)
        break
    except (ValueError, ImportError):
        pass


def data_dir():
    return os.path.join(GLib.get_user_data_dir(), "vibechat")


def config_dir():
    return os.path.join(GLib.get_user_config_dir(), "vibechat")


def autostart_file():
    return os.path.join(GLib.get_user_config_dir(), "autostart", APP_ID + ".desktop")


def auto_update_flag():
    return os.path.join(config_dir(), "auto-update-off")


def log(message):
    """A tray app should not die noisily; keep a small log instead."""
    try:
        os.makedirs(data_dir(), exist_ok=True)
        path = os.path.join(data_dir(), "error.log")
        if os.path.exists(path) and os.path.getsize(path) > 256 * 1024:
            os.remove(path)
        with open(path, "a", encoding="utf-8") as f:
            f.write(time.strftime("%Y-%m-%dT%H:%M:%S ") + message + "\n")
    except OSError:
        pass


class VibeChat(Gtk.Application):
    def __init__(self):
        super().__init__(application_id=APP_ID, flags=Gio.ApplicationFlags.HANDLES_COMMAND_LINE)
        self.add_main_option("hidden", 0, GLib.OptionFlags.NONE, GLib.OptionArg.NONE,
                             "Start in the tray without opening the window", None)
        self.add_main_option("version", 0, GLib.OptionFlags.NONE, GLib.OptionArg.NONE, "Show the version", None)
        self.window = None
        self.web = None
        self.indicator = None
        self.autostart_item = None
        self.unread = -1
        self.retry_id = 0
        self.web_restarts = 0
        self.quitting = False
        self.started = False
        self.notifications = {}
        self.state = {}
        self.update_item = None
        self.checking_update = False
        self.installing_update = False
        self.pending_deb = None             # downloaded and verified package waiting to be installed
        self.pending_version = None

    # ----- lifecycle -----
    def do_handle_local_options(self, options):
        if options.contains("version"):
            print(APP_NAME + " " + VERSION)
            return 0
        return -1

    def do_startup(self):
        Gtk.Application.do_startup(self)
        here = os.path.dirname(os.path.realpath(__file__))
        if os.path.isdir(os.path.join(here, "icons")):  # running from the source tree, not installed
            Gtk.IconTheme.get_default().append_search_path(os.path.join(here, "icons"))
        self.load_state()
        self.add_simple_action("show", lambda *_: self.show_window())
        self.add_simple_action("quit", lambda *_: self.quit_app())
        self.add_simple_action("hide", lambda *_: self.close_window())
        self.add_simple_action("reload", lambda *_: self.web.reload())
        self.add_simple_action("zoom-in", lambda *_: self.zoom(0.1))
        self.add_simple_action("zoom-out", lambda *_: self.zoom(-0.1))
        self.add_simple_action("zoom-reset", lambda *_: self.zoom(None))
        clicked = Gio.SimpleAction.new("notification-clicked", GLib.VariantType.new("t"))
        clicked.connect("activate", self.on_notification_clicked)
        self.add_action(clicked)
        self.add_simple_action("check-updates", lambda *_: self.check_for_updates(True))
        self.add_simple_action("install-update", lambda *_: self.install_update())
        GLib.timeout_add_seconds(FIRST_UPDATE_CHECK, self.on_first_update_check)
        for action, keys in (("quit", ["<Primary>q"]), ("hide", ["<Primary>w"]), ("reload", ["F5", "<Primary>r"]),
                             ("zoom-in", ["<Primary>plus", "<Primary>equal", "<Primary>KP_Add"]),
                             ("zoom-out", ["<Primary>minus", "<Primary>KP_Subtract"]),
                             ("zoom-reset", ["<Primary>0", "<Primary>KP_0"])):
            self.set_accels_for_action("app." + action, keys)
        self.build_window()
        if AppIndicator is not None:
            # Only show a tray icon when a tray really exists (Ubuntu's AppIndicator support, KDE, ...). Without one the
            # library falls back to a method that no longer works and the window could be hidden for good. At login the
            # tray may appear a moment after us, so watch for it rather than checking once.
            Gio.bus_watch_name(Gio.BusType.SESSION, "org.kde.StatusNotifierWatcher", Gio.BusNameWatcherFlags.NONE,
                               lambda *_: self.build_tray(), lambda *_: None)
        GLib.unix_signal_add(GLib.PRIORITY_DEFAULT, signal.SIGTERM, self.on_unix_signal)
        GLib.unix_signal_add(GLib.PRIORITY_DEFAULT, signal.SIGINT, self.on_unix_signal)
        self.web.load_uri(START_URL)

    def do_command_line(self, command_line):
        hidden = command_line.get_options_dict().contains("hidden")
        first = not self.started
        self.started = True
        if hidden:
            # --hidden is the login autostart: stay in the tray. If no tray turns up shortly, show the window instead,
            # since there would be no other way back to it. A second --hidden launch is ignored.
            if first and self.indicator is None:
                GLib.timeout_add_seconds(5, self.show_unless_tray)
            return 0
        self.show_window()
        return 0

    def show_unless_tray(self):
        if self.indicator is None and not self.quitting:
            self.show_window()
        return GLib.SOURCE_REMOVE

    def do_activate(self):
        self.show_window()

    def add_simple_action(self, name, callback):
        action = Gio.SimpleAction.new(name, None)
        action.connect("activate", callback)
        self.add_action(action)

    def on_unix_signal(self):
        self.quit_app()  # logout/shutdown: save state and close WebKit cleanly
        return GLib.SOURCE_REMOVE

    def quit_app(self):
        if self.quitting:
            return
        self.quitting = True
        self.save_state()
        self.withdraw_all_notifications()
        if self.window is not None:
            self.window.destroy()
        self.quit()

    # ----- window -----
    def build_window(self):
        win = Gtk.ApplicationWindow(application=self, title=APP_NAME)
        win.set_icon_name(ICON)
        win.set_default_size(self.state.get("width", 1100), self.state.get("height", 750))
        if self.state.get("maximized"):
            win.maximize()
        win.connect("delete-event", self.on_delete)
        self.web = self.create_web_view()
        win.add(self.web)
        self.web.show()
        self.window = win

    def show_window(self):
        self.window.present()  # also restores from minimised and raises it
        self.web.grab_focus()

    def close_window(self):
        if self.indicator is None:  # no tray to come back from: closing really quits
            self.quit_app()
        else:
            self.save_state()
            self.window.hide()

    def on_delete(self, *_):
        self.close_window()
        return True  # we decide whether to hide or quit

    def zoom(self, step):
        level = 1.0 if step is None else min(3.0, max(0.5, round(self.web.get_zoom_level() + step, 2)))
        self.web.set_zoom_level(level)
        self.state["zoom"] = level

    def load_state(self):
        try:
            with open(os.path.join(config_dir(), "state.json"), encoding="utf-8") as f:
                state = json.load(f)
            self.state = {k: v for k, v in state.items() if k in ("width", "height", "maximized", "zoom")}
            for k in ("width", "height"):
                if not isinstance(self.state.get(k), int) or not 400 <= self.state[k] <= 10000:
                    self.state.pop(k, None)
            if not isinstance(self.state.get("zoom"), (int, float)) or not 0.5 <= self.state["zoom"] <= 3.0:
                self.state.pop("zoom", None)
        except (OSError, ValueError, AttributeError):
            self.state = {}

    def save_state(self):
        if self.window is None:
            return
        maximized = self.window.is_maximized()
        if not maximized and self.window.get_visible():
            self.state["width"], self.state["height"] = self.window.get_size()
        self.state["maximized"] = maximized
        try:
            os.makedirs(config_dir(), exist_ok=True)
            with open(os.path.join(config_dir(), "state.json"), "w", encoding="utf-8") as f:
                json.dump(self.state, f)
        except OSError:
            pass

    # ----- web view -----
    def create_web_view(self):
        base = data_dir()
        os.makedirs(base, exist_ok=True)
        manager = WebKit2.WebsiteDataManager(base_data_directory=os.path.join(base, "web"),
                                             base_cache_directory=os.path.join(GLib.get_user_cache_dir(), "vibechat"))
        ctx = WebKit2.WebContext.new_with_website_data_manager(manager)
        ctx.get_cookie_manager().set_persistent_storage(os.path.join(base, "cookies.sqlite"),
                                                        WebKit2.CookiePersistentStorage.SQLITE)
        ctx.set_spell_checking_enabled(True)
        ctx.set_spell_checking_languages(spell_languages(GLib.get_language_names()))
        ctx.connect("initialize-notification-permissions", self.on_init_notification_permissions)
        ctx.connect("download-started", self.on_download_started)

        web = WebKit2.WebView(web_context=ctx)
        settings = web.get_settings()
        settings.set_user_agent(USER_AGENT)
        settings.set_enable_developer_extras(False)
        settings.set_enable_media_stream(True)                 # microphone for voice messages
        settings.set_javascript_can_access_clipboard(True)     # copy buttons
        rgba = Gdk.RGBA()
        rgba.red, rgba.green, rgba.blue, rgba.alpha = BACKGROUND + (1.0,)
        web.set_background_color(rgba)
        web.set_zoom_level(self.state.get("zoom", 1.0))
        web.connect("decide-policy", self.on_decide_policy)
        web.connect("load-changed", self.on_load_changed)
        web.connect("load-failed", self.on_load_failed)
        web.connect("notify::title", self.on_title)
        web.connect("permission-request", self.on_permission_request)
        web.connect("show-notification", self.on_show_notification)
        web.connect("web-process-terminated", self.on_web_process_terminated)
        return web

    def on_decide_policy(self, web, decision, kind):
        if kind == WebKit2.PolicyDecisionType.NEW_WINDOW_ACTION:
            # target=_blank links and window.open: always the default browser
            uri = decision.get_navigation_action().get_request().get_uri()
            decision.ignore()
            self.open_external(uri)
            return True
        if kind == WebKit2.PolicyDecisionType.NAVIGATION_ACTION:
            action = decision.get_navigation_action()
            uri = action.get_request().get_uri()
            # Only clicked links are redirected; frames that WhatsApp embeds load normally.
            if action.get_navigation_type() == WebKit2.NavigationType.LINK_CLICKED and not is_app_page(uri):
                decision.ignore()
                self.open_external(uri)
                return True
        return False  # everything else, including files the view can't show (downloaded), uses WebKit's default

    def open_external(self, uri):
        if not is_external_scheme(uri):
            return
        try:
            Gtk.show_uri_on_window(self.window, uri, Gdk.CURRENT_TIME)
        except GLib.Error as e:
            log("could not open %s: %s" % (uri, e.message))

    def on_load_changed(self, web, event):
        if event == WebKit2.LoadEvent.COMMITTED and is_app_origin(web.get_uri()):
            self.cancel_retry()  # a real WhatsApp page arrived (e.g. after a manual reload)

    def on_load_failed(self, web, event, uri, error):
        if error.matches(WebKit2.network_error_quark(), WebKit2.NetworkError.CANCELLED) or \
                error.matches(WebKit2.policy_error_quark(), WebKit2.PolicyError.FRAME_LOAD_INTERRUPTED_BY_POLICY_CHANGE):
            return False  # we (or a newer navigation) stopped it, or it became a download
        if not is_app_origin(uri):
            return False
        # offline or WhatsApp unreachable: show a calm page and keep retrying
        GLib.idle_add(self.show_offline_page)
        self.schedule_retry()
        return True

    def show_offline_page(self):
        if not self.quitting:
            self.web.load_html(OFFLINE_HTML, None)
        return GLib.SOURCE_REMOVE

    def schedule_retry(self):
        if not self.retry_id:
            self.retry_id = GLib.timeout_add_seconds(RETRY_SECONDS, self.retry)

    def cancel_retry(self):
        if self.retry_id:
            GLib.source_remove(self.retry_id)
            self.retry_id = 0

    def retry(self):
        self.retry_id = 0
        if not self.quitting and not is_app_origin(self.web.get_uri()):
            self.web.load_uri(START_URL)
        return GLib.SOURCE_REMOVE

    def on_web_process_terminated(self, web, reason):
        if self.quitting or reason == WebKit2.WebProcessTerminationReason.TERMINATED_BY_API:
            return
        self.web_restarts += 1
        if self.web_restarts > MAX_WEB_RESTARTS:
            log("web process keeps exiting; giving up after %d restarts" % MAX_WEB_RESTARTS)
            return
        log("web process exited (%s); reloading (restart %d)" % (reason.value_nick, self.web_restarts))
        GLib.idle_add(self.reload_app)

    def reload_app(self):
        if not self.quitting:
            self.web.load_uri(START_URL)
        return GLib.SOURCE_REMOVE

    def on_permission_request(self, web, request):
        kinds = [WebKit2.NotificationPermissionRequest, WebKit2.UserMediaPermissionRequest]
        clipboard = getattr(WebKit2, "ClipboardPermissionRequest", None)  # WebKitGTK 2.42+
        if clipboard is not None:
            kinds.append(clipboard)
        if is_app_origin(web.get_uri()) and isinstance(request, tuple(kinds)):
            request.allow()
        else:
            request.deny()
        return True

    def on_init_notification_permissions(self, ctx):
        # Grant WhatsApp Web notifications up front, so it does not keep asking.
        ctx.initialize_notification_permissions([WebKit2.SecurityOrigin.new_for_uri(START_URL)], [])

    # ----- unread count: window title, tray icon, dock badge -----
    def on_title(self, web, _pspec):
        n = unread_count(web.get_title())
        if n == self.unread:
            return
        self.unread = n
        self.window.set_title("(%d) %s" % (n, APP_NAME) if n else APP_NAME)
        if self.indicator is not None:
            self.indicator.set_icon_full(ICON_UNREAD if n else ICON, "%d unread" % n if n else APP_NAME)
            self.indicator.set_title("%s - %d unread" % (APP_NAME, n) if n else APP_NAME)
        self.update_dock_badge(n)

    def update_dock_badge(self, n):
        """Ubuntu Dock (and other docks) show a count on the launcher via the Unity LauncherEntry signal."""
        try:
            bus = self.get_dbus_connection()
            if bus is None:
                return
            props = {"count": GLib.Variant("x", n), "count-visible": GLib.Variant("b", n > 0)}
            bus.emit_signal(None, "/" + APP_ID.replace(".", "/"), "com.canonical.Unity.LauncherEntry", "Update",
                            GLib.Variant("(sa{sv})", ("application://" + APP_ID + ".desktop", props)))
        except GLib.Error as e:
            log("dock badge: " + e.message)

    # ----- notifications -----
    def on_show_notification(self, web, notification):
        nid = notification.get_id()
        self.notifications[nid] = notification
        notification.connect("closed", self.on_notification_closed)
        n = Gio.Notification.new(notification.get_title() or APP_NAME)
        if notification.get_body():
            n.set_body(notification.get_body())
        n.set_icon(Gio.ThemedIcon.new(ICON))
        n.set_default_action_and_target_value("app.notification-clicked", GLib.Variant("t", nid))
        self.send_notification(self.notification_key(notification), n)
        return True

    @staticmethod
    def notification_key(notification):
        tag = notification.get_tag()  # same tag = same conversation: replace rather than stack
        return "tag-" + tag if tag else "msg-%d" % notification.get_id()

    def on_notification_closed(self, notification):
        self.withdraw_notification(self.notification_key(notification))
        self.notifications.pop(notification.get_id(), None)

    def on_notification_clicked(self, _action, param):
        notification = self.notifications.pop(param.get_uint64(), None)
        self.show_window()
        if notification is not None:
            notification.clicked()  # WhatsApp opens that chat

    def withdraw_all_notifications(self):
        for notification in list(self.notifications.values()):
            self.withdraw_notification(self.notification_key(notification))
        self.notifications.clear()

    # ----- downloads -----
    def on_download_started(self, ctx, download):
        download.connect("decide-destination", self.on_decide_destination)
        download.connect("finished", self.on_download_finished)

    def on_decide_destination(self, download, suggested):
        folder = GLib.get_user_special_dir(GLib.UserDirectory.DIRECTORY_DOWNLOAD) or os.path.expanduser("~")
        os.makedirs(folder, exist_ok=True)
        download.set_destination(GLib.filename_to_uri(unique_path(folder, suggested), None))
        return True

    def on_download_finished(self, download):
        dest = download.get_destination()
        if not dest:
            return
        n = Gio.Notification.new("Download complete")
        n.set_body(os.path.basename(GLib.filename_from_uri(dest)[0]))
        n.set_icon(Gio.ThemedIcon.new(ICON))
        self.send_notification("download", n)

    # ----- updates -----
    def on_first_update_check(self):
        self.on_periodic_update_check()
        GLib.timeout_add_seconds(UPDATE_CHECK_INTERVAL, self.on_periodic_update_check)
        return GLib.SOURCE_REMOVE

    def on_periodic_update_check(self):
        if not os.path.exists(auto_update_flag()):
            self.check_for_updates(False)
        return GLib.SOURCE_CONTINUE

    def check_for_updates(self, manual):
        if self.checking_update or self.installing_update or self.quitting:
            return
        if self.pending_deb:
            if manual:
                self.offer_update()
            return
        if parse_version(VERSION) is None:  # running unbuilt source: nothing to compare against
            return
        self.checking_update = True
        self.set_update_item("Checking for updates…", False)
        folder = os.path.join(GLib.get_user_cache_dir(), "vibechat", "update")
        threading.Thread(target=self.update_worker, args=(manual, folder), daemon=True).start()

    def update_worker(self, manual, folder):  # runs off the GTK thread
        latest = deb = error = None
        try:
            latest = fetch_latest_version()
            if latest and latest > parse_version(VERSION):
                deb = download_update(latest, folder)
        except Exception as e:  # network, HTTP or verification problems all just mean "not this time"
            error = str(e)
            log("update check failed: %s" % e)
        GLib.idle_add(self.update_check_done, manual, latest, deb, error)

    def update_check_done(self, manual, latest, deb, error):
        self.checking_update = False
        self.set_update_item("Check for updates", True)
        if deb:
            self.pending_deb, self.pending_version = deb, latest
            log("update %s downloaded and verified" % format_version(latest))
            self.set_update_item("Install update %s…" % format_version(latest), True)
            self.offer_update()
        elif manual:
            n = Gio.Notification.new("Couldn't check for updates" if error else APP_NAME + " is up to date")
            n.set_body(error or "You have version %s." % VERSION)
            n.set_icon(Gio.ThemedIcon.new(ICON))
            self.send_notification("updates", n)
        return GLib.SOURCE_REMOVE

    def offer_update(self):
        n = Gio.Notification.new("%s %s is ready to install" % (APP_NAME, format_version(self.pending_version)))
        n.set_body("Your password is needed to install it. %s restarts afterwards." % APP_NAME)
        n.set_icon(Gio.ThemedIcon.new(ICON))
        n.add_button("Install", "app.install-update")
        n.set_default_action("app.install-update")
        self.send_notification("updates", n)

    def install_update(self):
        if not self.pending_deb or self.installing_update or self.quitting:
            return
        if not os.path.exists(self.pending_deb):
            self.pending_deb = None
            self.set_update_item("Check for updates", True)
            return
        self.withdraw_notification("updates")
        pkexec = GLib.find_program_in_path("pkexec")
        if not pkexec:  # no polkit: hand the package to the system's software installer instead
            Gio.AppInfo.launch_default_for_uri(GLib.filename_to_uri(self.pending_deb, None), None)
            return
        self.installing_update = True
        self.set_update_item("Installing update…", False)
        # pkexec shows the system's password prompt; apt-get installs the verified package like any other.
        proc = Gio.Subprocess.new([pkexec, "apt-get", "install", "-y", self.pending_deb],
                                  Gio.SubprocessFlags.STDOUT_SILENCE | Gio.SubprocessFlags.STDERR_PIPE)
        proc.communicate_utf8_async(None, None, self.on_update_installed)

    def on_update_installed(self, proc, result):
        self.installing_update = False
        try:
            _, _, err = proc.communicate_utf8_finish(result)
        except GLib.Error as e:
            err = e.message
        code = proc.get_exit_status() if proc.get_if_exited() else -1
        if code == 0:
            log("updated to %s; restarting" % format_version(self.pending_version))
            self.restart()
            return
        log("update not installed (exit %d): %s" % (code, (err or "").strip()[-400:]))
        self.set_update_item("Install update %s…" % format_version(self.pending_version), True)
        n = Gio.Notification.new(APP_NAME + " wasn't updated")
        n.set_body("The password prompt was cancelled." if code in (126, 127) else "The update couldn't be installed.")
        n.set_icon(Gio.ThemedIcon.new(ICON))
        self.send_notification("updates", n)

    def restart(self):
        hidden = not self.window.get_visible()
        exe = shutil.which("vibechat") or "/usr/bin/vibechat"
        # The new copy starts once this one has gone, so it becomes the single running instance.
        Gio.Subprocess.new(["/bin/sh", "-c", "sleep 2; exec %s%s" % (shlex.quote(exe), " --hidden" if hidden else "")],
                           Gio.SubprocessFlags.NONE)
        self.quit_app()

    def set_update_item(self, label, sensitive):
        if self.update_item is not None:
            self.update_item.set_label(label)
            self.update_item.set_sensitive(sensitive)

    def on_update_item(self, *_):
        if self.pending_deb:
            self.install_update()
        else:
            self.check_for_updates(True)

    def on_auto_update_toggled(self, item):
        path = auto_update_flag()
        try:
            if item.get_active():
                if os.path.exists(path):
                    os.remove(path)
            else:
                os.makedirs(os.path.dirname(path), exist_ok=True)
                with open(path, "w", encoding="utf-8") as f:
                    f.write("Automatic update checks are off. Use the tray menu, or delete this file, to turn them back on.\n")
        except OSError as e:
            log("auto-update setting: %s" % e)

    # ----- tray -----
    def build_tray(self):
        if AppIndicator is None or self.indicator is not None or self.quitting:
            return
        ind = AppIndicator.Indicator.new(APP_ID, ICON, AppIndicator.IndicatorCategory.COMMUNICATIONS)
        here = os.path.dirname(os.path.realpath(__file__))
        if os.path.isdir(os.path.join(here, "icons")):
            ind.set_icon_theme_path(os.path.join(here, "icons"))
        ind.set_title(APP_NAME)
        menu = Gtk.Menu()
        open_item = Gtk.MenuItem(label="Open " + APP_NAME)
        open_item.connect("activate", lambda *_: self.show_window())
        self.autostart_item = Gtk.CheckMenuItem(label="Start on login")
        self.autostart_item.set_active(os.path.exists(autostart_file()))
        self.autostart_item.connect("toggled", self.on_autostart_toggled)
        version_item = Gtk.MenuItem(label="%s %s" % (APP_NAME, VERSION))
        version_item.set_sensitive(False)
        self.update_item = Gtk.MenuItem(label="Check for updates")
        self.update_item.connect("activate", self.on_update_item)
        if self.pending_deb:  # an update may have been found before the tray appeared
            self.update_item.set_label("Install update %s…" % format_version(self.pending_version))
        auto_update_item = Gtk.CheckMenuItem(label="Update automatically")
        auto_update_item.set_active(not os.path.exists(auto_update_flag()))
        auto_update_item.connect("toggled", self.on_auto_update_toggled)
        quit_item = Gtk.MenuItem(label="Quit")
        quit_item.connect("activate", lambda *_: self.quit_app())
        for item in (open_item, self.autostart_item, Gtk.SeparatorMenuItem(), version_item, self.update_item,
                     auto_update_item, Gtk.SeparatorMenuItem(), quit_item):
            menu.append(item)
        menu.show_all()
        ind.set_menu(menu)
        ind.set_secondary_activate_target(open_item)  # middle-click the tray icon opens the window
        ind.set_status(AppIndicator.IndicatorStatus.ACTIVE)
        if self.unread > 0:  # messages may have arrived before the tray did
            ind.set_icon_full(ICON_UNREAD, "%d unread" % self.unread)
        self.indicator = ind

    def on_autostart_toggled(self, item):
        path = autostart_file()
        try:
            if item.get_active():
                os.makedirs(os.path.dirname(path), exist_ok=True)
                exe = os.path.realpath(sys.argv[0])
                with open(path, "w", encoding="utf-8") as f:
                    f.write("[Desktop Entry]\nType=Application\nName=%s\nExec=%s --hidden\nIcon=%s\n"
                            "X-GNOME-Autostart-enabled=true\n" % (APP_NAME, desktop_exec_quote(exe), ICON))
            elif os.path.exists(path):
                os.remove(path)
        except OSError as e:
            log("autostart: %s" % e)
            item.set_active(os.path.exists(path))


def main():
    GLib.set_prgname(APP_ID)  # window class matches the .desktop file, so the dock groups it correctly
    GLib.set_application_name(APP_NAME)
    return VibeChat().run(sys.argv)


if __name__ == "__main__":
    sys.exit(main())
