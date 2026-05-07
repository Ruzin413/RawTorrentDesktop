# 🌊 RawTorrent

**RawTorrent** is a sleek, modern, and high-performance torrent client built with C# and WPF. It features a premium design, real-time download tracking, and a self-contained engine that makes it easy to install and run on any Windows machine.

![RawTorrent Icon](TorServices/TorServices/Resources/app_icon.png)

## ✨ Features

- **Modern UI**: A clean, blue-themed interface with glassmorphism elements.
- **Magnet & Torrent Support**: Easily add downloads via magnet links or `.torrent` files.
- **Smart Port Selection**: Automatically handles port collisions to ensure the backend service always starts.
- **Persistence**: Remembers your download history and progress using a robust CSV-based storage.
- **Self-Contained**: No need to install the .NET runtime separately; everything is bundled in the app.
- **Professional Installer**: Comes with a setup wizard for easy installation and desktop shortcut creation.

## 🚀 Getting Started

### Installation
1. Download the latest **`RawTorrentSetup.exe`** from the [Releases](https://github.com/Ruzin413/RawTorrentDesktop/releases) page.
2. Run the installer and follow the wizard.
3. Launch **RawTorrent** from your desktop!

### Building from Source
If you want to contribute or build the app yourself:
1. Clone the repository:
   ```bash
   git clone https://github.com/Ruzin413/RawTorrentDesktop.git
   ```
2. Open the solution in **Visual Studio 2022**.
3. Restore NuGet packages and build the project.
4. To create a single-file EXE, run the provided publish script:
   ```powershell
   cd TorServices/TorServices
   ./publish.ps1
   ```

## 🛠️ Tech Stack

- **Frontend**: WPF (Windows Presentation Foundation)
- **Backend**: ASP.NET Core (Hosted within the WPF app)
- **Data**: CSV-based persistence for lightweight, no-DB storage.
- **Installer**: Inno Setup

## 👤 Author

**Rujin Manandhar**
- 📧 [rzmdr413@gmail.com](mailto:rzmdr413@gmail.com)
- 📱 9813806527
- 🐙 [GitHub](https://github.com/Ruzin413)
- 🔗 [LinkedIn](https://www.linkedin.com/in/ruzin-mdr-393bb9380/)
- 📍 Kathmandu, Nepal

## 📄 License

This project is licensed under the MIT License - see the [LICENSE](LICENSE) file for details.

