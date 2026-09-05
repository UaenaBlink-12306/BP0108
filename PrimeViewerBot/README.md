# 📺 Universal Viewer Bot by PrimeEcto

![Version](https://img.shields.io/badge/version-4.0-blue?style=for-the-badge&logo=appveyor)
![Python](https://img.shields.io/badge/python-3.10%2B-yellow?style=for-the-badge&logo=python&logoColor=white)
![Platform](https://img.shields.io/badge/platform-Windows-brightgreen?style=for-the-badge&logo=windows)
![License](https://img.shields.io/badge/license-MIT-orange?style=for-the-badge)

An advanced, **GUI-based** Viewer Bot designed to simulate active traffic using real browser sessions. Now featuring a modern Dark Mode interface, **Universal URL support** (Twitch, Kick, YouTube), and intelligent proxy rotation.

**No proxy lists. No command line. No complex setup.** Just open the app, paste your link, and go.

---

## ⚡ What's New in v4.0

### 🎨 Modern Dark UI
Forget black command prompt windows. The bot now runs in a sleek, user-friendly application window built with **CustomTkinter**.

### 🌐 Universal URL Support
No longer limited to just Twitch!
- **Works on:** Twitch, Kick, YouTube, and most other streaming platforms.
- Simply paste the **full link** (e.g., `https://kick.com/username`) and the bot handles the rest.

### 🔀 Hybrid Proxy Mode
**Bypass the "1 IP Limit" with a single click.**
- **On:** Rotates every new viewer through a different proxy server (Croxy → BlockAway → YouTubeUnblocked).
- **Off:** Sticks to a single server of your choice.
- *Result: Higher viewer retention and harder detection.*

---

## 🔧 Core Features

- ✅ **No Proxy Setup Needed** Pre-integrated with multiple public proxy services — no configuration or sourcing required.

- 🧠 **Intelligent Window Handling** Supports multiple concurrent viewer sessions in separate browser tabs to mimic distinct users.

- 📦 **Auto-Driver System** No manual downloads required. The bot automatically detects your Chrome version and installs the matching `chromedriver.exe`.

- 🛠 **Headless Mode 2.0** Run sessions invisibly in the background using the new `--headless=new` flag, complete with GPU acceleration fixes to remain undetected.

- 🧼 **Clean Console** Built-in log window inside the app to track viewer status in real-time.

- 🧩 **Extension Support** Optional `adblock.crx` support for cleaner, faster loading sessions.

---

## 📥 Installation Instructions

1. **Download** the latest `.zip` file from the [Releases](../../releases) section.
2. **Extract** the contents using 7-Zip or WinRAR.
3. **Install Dependencies:** Double-click the included `install.bat` file.
   > This will automatically run `pip install -r requirements.txt` for you.
4. **Launch:** Double-click `run.bat` to start the bot.

---

## ▶️ How to Use

1. **Target URL:** Paste the full link you want to view (e.g., `https://twitch.tv/PrimeEcto`).

2. **Proxy Configuration:** - Toggle **Hybrid Mode** (Recommended) to rotate through all available servers.
   - Or, select a specific server from the dropdown list.

3. **Viewer Count:** Use the slider to set the number of concurrent tabs/viewers.

4. **Browser Mode:** - Toggle **"Hide Browser"** ON to run in the background (saves RAM).
   - Toggle OFF to watch the tabs open in real-time.

5. **Start:** Click the **START BOT** button. The internal console will log connection attempts.

---

## 📝 Notes & Disclaimer

- **Educational Use Only:** This tool is designed for testing stream behavior, overlays, and browser automation handling.
- **Hardware Requirements:** Running 10+ browser tabs consumes significant RAM. Use "Headless Mode" for better performance.
- **Liability:** The developer is not responsible for any misuse of this tool or potential account actions taken by streaming platforms.

---