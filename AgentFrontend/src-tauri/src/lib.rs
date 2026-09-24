use std::sync::Mutex;
use std::net::TcpListener;
use tauri::menu::{Menu, MenuItem};
use tauri::tray::{MouseButton, MouseButtonState, TrayIconBuilder, TrayIconEvent};
use tauri::{Manager, RunEvent, WindowEvent};
use tauri_plugin_shell::process::{CommandChild, CommandEvent};
use tauri_plugin_shell::ShellExt;

struct BackendProcess(Mutex<Option<CommandChild>>);
struct BackendEndpoint(Mutex<String>);

/// Returns the loopback URL reserved for the managed local API process.
#[tauri::command]
fn get_backend_url(endpoint: tauri::State<'_, BackendEndpoint>) -> String {
    endpoint.0.lock().map(|url| url.clone()).unwrap_or_else(|_| "http://127.0.0.1:5008".to_string())
}

/// Starts the packaged local API, builds the tray menu, and manages desktop shutdown.
#[cfg_attr(mobile, tauri::mobile_entry_point)]
pub fn run() {
    let application = tauri::Builder::default()
        .plugin(tauri_plugin_dialog::init())
        .plugin(tauri_plugin_shell::init())
        .invoke_handler(tauri::generate_handler![get_backend_url])
        .setup(|app| {
            app.manage(BackendEndpoint(Mutex::new("http://127.0.0.1:5008".to_string())));
            if cfg!(debug_assertions) {
                app.handle().plugin(
                    tauri_plugin_log::Builder::default()
                        .level(log::LevelFilter::Info)
                        .build(),
                )?;
            } else {
                start_backend(app)?;
            }

            install_tray(app)?;
            Ok(())
        })
        .on_window_event(|window, event| {
            if let WindowEvent::CloseRequested { api, .. } = event {
                api.prevent_close();
                let _ = window.hide();
            }
        });

    application
        .build(tauri::generate_context!())
        .expect("failed to build Lucas Agent")
        .run(|app, event| {
            if matches!(event, RunEvent::Exit) {
                stop_backend(app);
            }
        });
}

/// Launches the self-contained ASP.NET sidecar and drains its output streams.
fn start_backend(app: &mut tauri::App) -> Result<(), Box<dyn std::error::Error>> {
    let reserved_listener = TcpListener::bind("127.0.0.1:0")?;
    let port = reserved_listener.local_addr()?.port();
    drop(reserved_listener);
    let backend_url = format!("http://127.0.0.1:{port}");
    if let Some(endpoint) = app.try_state::<BackendEndpoint>() {
        *endpoint.0.lock().map_err(|_| "backend URL state is poisoned")? = backend_url.clone();
    }
    let (mut events, child) = app
        .shell()
        .sidecar("lucas-agent-backend")?
        .args(["--urls".to_string(), backend_url])
        .spawn()?;

    tauri::async_runtime::spawn(async move {
        while let Some(event) = events.recv().await {
            match event {
                CommandEvent::Stdout(bytes) => log::info!("backend: {}", String::from_utf8_lossy(&bytes)),
                CommandEvent::Stderr(bytes) => log::warn!("backend: {}", String::from_utf8_lossy(&bytes)),
                CommandEvent::Terminated(payload) => log::info!("backend exited: {payload:?}"),
                _ => {}
            }
        }
    });

    app.manage(BackendProcess(Mutex::new(Some(child))));
    Ok(())
}

/// Creates a tray icon with show and quit actions for the desktop application.
fn install_tray(app: &mut tauri::App) -> Result<(), Box<dyn std::error::Error>> {
    let show = MenuItem::with_id(app, "show", "打开 Lucas Agent", true, None::<&str>)?;
    let quit = MenuItem::with_id(app, "quit", "退出 Lucas Agent", true, None::<&str>)?;
    let menu = Menu::with_items(app, &[&show, &quit])?;

    let mut tray = TrayIconBuilder::new()
        .menu(&menu)
        .show_menu_on_left_click(false)
        .on_menu_event(|app, event| match event.id().as_ref() {
            "show" => reveal_main_window(app),
            "quit" => app.exit(0),
            _ => {}
        })
        .on_tray_icon_event(|tray, event| {
            if let TrayIconEvent::Click {
                button: MouseButton::Left,
                button_state: MouseButtonState::Up,
                ..
            } = event
            {
                reveal_main_window(tray.app_handle());
            }
        });

    if let Some(icon) = app.default_window_icon() {
        tray = tray.icon(icon.clone());
    }
    tray.build(app)?;
    Ok(())
}

/// Shows and focuses the main window after a tray interaction.
fn reveal_main_window(app: &tauri::AppHandle) {
    if let Some(window) = app.get_webview_window("main") {
        let _ = window.show();
        let _ = window.set_focus();
    }
}

/// Stops the managed backend process when the user exits from the tray menu.
fn stop_backend(app: &tauri::AppHandle) {
    if let Some(state) = app.try_state::<BackendProcess>() {
        if let Ok(mut process) = state.0.lock() {
            if let Some(child) = process.take() {
                let _ = child.kill();
            }
        }
    }
}
