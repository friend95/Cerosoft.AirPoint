# Cerosoft AirPoint Server

> **System‑level input relay server for AirPoint**  
> Precision. Low latency. Built for production.

---

## Overview
**Cerosoft AirPoint Server** is a Windows‑based server application that acts as the backbone of the AirPoint ecosystem. It receives remote input signals (mouse, gestures, control events) and translates them into native Windows actions with minimal latency and high reliability.

This project is designed like a real system tool — not a toy app. Clean WPF UI, strict separation of concerns, and production‑ready configuration handling.

---

## Key Features
- ⚡ **Low‑latency input processing**
- 🖱️ **Remote mouse & gesture handling**
- 🪟 **Native Windows integration**
- 🔒 **Config‑driven behavior (AppSettings)**
- 🎛️ **Settings dialog with persistent state**
- 🧠 **Minimal UI overhead – server‑first design**

---

## Related Repositories

### Client (Android)
The Android client application that connects to this server:

👉 https://github.com/friend95/Cerosoft.AirPoint.Client

Use this repository alongside the server for the complete AirPoint experience.

---

## Tech Stack
- **.NET (WPF)**
- **C#**
- **XAML (MVVM‑friendly layout)**
- **Windows Desktop APIs**

---

## Project Structure
```
Cerosoft.AirPoint.Server
│
├── App.xaml                # Application bootstrap & resources
├── App.xaml.cs             # App lifecycle logic
├── MainWindow.xaml         # Server control UI
├── MainWindow.xaml.cs      # Core server logic + UI binding
├── SettingsDialog.xaml     # Configuration UI
├── SettingsDialog.xaml.cs  # Settings logic
├── AppSettings.cs          # Centralized configuration model
├── app.manifest            # Windows execution & DPI settings
└── Cerosoft.AirPoint.Server.csproj
```

---

## Getting Started

### Prerequisites
- Windows 10 / 11
- Visual Studio 2022+ (with **.NET Desktop Development** workload)
- .NET Desktop Runtime (matching project target)

### Build & Run
```bash
# Clone the repository
git clone https://github.com/friend95/Cerosoft.AirPoint.git

# Open the solution in Visual Studio
# Restore NuGet packages
# Build → Run
```

The server will launch with a lightweight control UI and run persistently in the background.

---

## Configuration
All runtime‑tunable values are centralized in:
```
AppSettings.cs
```

This keeps behavior deterministic, debuggable, and production‑safe. No magic constants scattered across the codebase.

---

## Design Philosophy
- **System‑tool mindset** (like a driver, not a consumer app)
- **Predictable execution > flashy UI**
- **Fail‑safe defaults**
- **Zero unnecessary dependencies**

This is intentional. Reliability beats gimmicks.

---

## Security Notes
- Designed to run on trusted local networks
- No hidden background services
- No telemetry

If you extend networking features, apply proper authentication and encryption.

---

## Roadmap
- [ ] Headless / tray‑only mode
- [ ] Auto‑start on boot
- [ ] Encrypted client‑server handshake
- [ ] Plugin‑based gesture system

---

## Contribution Guidelines
Pull requests are welcome, but keep it **clean and intentional**:
- No dead code
- No UI bloat
- No silent behavior changes

---

## License
This project is licensed under the **MIT License**.

---

## Author
**Cerosoft**  
Built with a systems‑engineering mindset.

---

> If it doesn’t feel like it could ship to millions of machines, it doesn’t belong here.

