import customtkinter as ctk
import threading
import time
import random
import gc
from concurrent.futures import ThreadPoolExecutor, as_completed
from datetime import datetime
from selenium import webdriver
from selenium.webdriver.common.keys import Keys
from selenium.webdriver.common.by import By
from selenium.webdriver.support.ui import WebDriverWait
from selenium.webdriver.support import expected_conditions as EC
from selenium.webdriver.chrome.service import Service
from webdriver_manager.chrome import ChromeDriverManager
from selenium.common.exceptions import TimeoutException, WebDriverException, NoSuchElementException
import urllib3
urllib3.disable_warnings()

# --- THEME ---
ctk.set_appearance_mode("Dark")
ctk.set_default_color_theme("green")


class ViewerBotApp(ctk.CTk):
    def __init__(self):
        super().__init__()

        # Window Setup
        self.title("Prime Viewer Bot v5.0 - Enhanced")
        self.geometry("720x800")
        self.resizable(False, False)
        self.protocol("WM_DELETE_WINDOW", self.on_closing)

        # Thread-safe variables
        self.lock = threading.Lock()
        self.drivers = []
        self.is_running = False
        self.success_count = 0
        self.failed_count = 0
        self.active_count = 0
        self.driver_manager_path = None  # Cache driver path

        # Expanded Proxy List (No file needed!)
        self.proxy_map = {
            "Server 1 (CroxyProxy)": "https://www.croxyproxy.com",
            "Server 2 (Croxy.Rocks)": "https://www.croxyproxy.rocks",
            "Server 3 (BlockAway)": "https://www.blockaway.net",
            "Server 4 (Croxy.Network)": "https://www.croxy.network",
            "Server 5 (Croxy.Org)": "https://www.croxy.org",
            "Server 6 (YouTubeUnblocked)": "https://www.youtubeunblocked.live",
            "Server 7 (Plain Proxy)": "https://plainproxy.com",
            "Server 8 (Hide.me)": "https://hide.me/en/proxy"
        }
        self.proxy_urls = list(self.proxy_map.values())

        # User Agents for Rotation (Anti-detection)
        self.user_agents = [
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/121.0.0.0 Safari/537.36",
            "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:123.0) Gecko/20100101 Firefox/123.0",
            "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36 Edg/120.0.0.0",
        ]

        # Build UI
        self._create_ui()
        
        # Pre-download ChromeDriver in background
        threading.Thread(target=self._preload_driver, daemon=True).start()

    def _preload_driver(self):
        """Pre-download ChromeDriver to avoid delays later"""
        try:
            self.driver_manager_path = ChromeDriverManager().install()
            self.log("✅ ChromeDriver ready")
        except Exception as e:
            self.log(f"⚠️ Driver pre-load failed: {str(e)[:40]}")

    def _create_ui(self):
        # === TITLE ===
        self.title_label = ctk.CTkLabel(
            self, 
            text="🎮 UNIVERSAL VIEWER BOT", 
            font=("Segoe UI", 28, "bold"), 
            text_color="#00ff99"
        )
        self.title_label.pack(pady=(20, 5))
        
        self.subtitle_label = ctk.CTkLabel(
            self, 
            text="Twitch • YouTube • Kick • Any Platform", 
            font=("Segoe UI", 12), 
            text_color="#888888"
        )
        self.subtitle_label.pack(pady=(0, 15))

        # === INPUT FRAME ===
        self.input_frame = ctk.CTkFrame(self)
        self.input_frame.pack(pady=10, padx=25, fill="x")

        # Target URL
        self.url_label = ctk.CTkLabel(
            self.input_frame, 
            text="🎯 Target URL:", 
            font=("Segoe UI", 14, "bold")
        )
        self.url_label.pack(pady=(15, 5), anchor="w", padx=15)
        
        self.url_entry = ctk.CTkEntry(
            self.input_frame, 
            placeholder_text="https://twitch.tv/username or any streaming link...", 
            height=45, 
            font=("Segoe UI", 13)
        )
        self.url_entry.pack(pady=5, padx=15, fill="x")

        # Proxy Server Selection
        self.proxy_label = ctk.CTkLabel(
            self.input_frame, 
            text="🌐 Proxy Server:", 
            font=("Segoe UI", 14, "bold")
        )
        self.proxy_label.pack(pady=(15, 5), anchor="w", padx=15)
        
        self.proxy_dropdown = ctk.CTkOptionMenu(
            self.input_frame, 
            values=list(self.proxy_map.keys()), 
            height=40,
            font=("Segoe UI", 12)
        )
        self.proxy_dropdown.pack(pady=5, padx=15, fill="x")

        # === SETTINGS FRAME ===
        self.settings_frame = ctk.CTkFrame(self.input_frame, fg_color="transparent")
        self.settings_frame.pack(pady=15, padx=10, fill="x")

        # Left Column
        self.left_settings = ctk.CTkFrame(self.settings_frame, fg_color="transparent")
        self.left_settings.pack(side="left", expand=True, fill="x")

        self.hybrid_switch = ctk.CTkSwitch(
            self.left_settings, 
            text="🔄 Hybrid Mode", 
            command=self._toggle_hybrid,
            font=("Segoe UI", 12)
        )
        self.hybrid_switch.pack(pady=5, anchor="w")

        self.headless_switch = ctk.CTkSwitch(
            self.left_settings, 
            text="👁️ Hide Browser", 
            font=("Segoe UI", 12)
        )
        self.headless_switch.select()
        self.headless_switch.pack(pady=5, anchor="w")

        # Right Column
        self.right_settings = ctk.CTkFrame(self.settings_frame, fg_color="transparent")
        self.right_settings.pack(side="right", expand=True, fill="x")

        self.refresh_switch = ctk.CTkSwitch(
            self.right_settings, 
            text="🔃 Auto-Refresh", 
            font=("Segoe UI", 12)
        )
        self.refresh_switch.pack(pady=5, anchor="w")

        self.stealth_switch = ctk.CTkSwitch(
            self.right_settings, 
            text="🥷 Stealth Mode", 
            font=("Segoe UI", 12)
        )
        self.stealth_switch.select()
        self.stealth_switch.pack(pady=5, anchor="w")

        # === SLIDERS ===
        self.sliders_frame = ctk.CTkFrame(self)
        self.sliders_frame.pack(pady=10, padx=25, fill="x")

        # Viewers Slider
        self.viewers_label = ctk.CTkLabel(
            self.sliders_frame, 
            text="👥 Viewers: 5", 
            font=("Segoe UI", 14, "bold")
        )
        self.viewers_label.pack(pady=(15, 5), anchor="w", padx=15)
        
        self.viewers_slider = ctk.CTkSlider(
            self.sliders_frame, 
            from_=1, 
            to=30, 
            number_of_steps=29, 
            command=self._update_viewers_label
        )
        self.viewers_slider.set(5)
        self.viewers_slider.pack(pady=5, padx=15, fill="x")

        # Threads Slider
        self.threads_label = ctk.CTkLabel(
            self.sliders_frame, 
            text="⚡ Parallel Threads: 3", 
            font=("Segoe UI", 14, "bold")
        )
        self.threads_label.pack(pady=(15, 5), anchor="w", padx=15)
        
        self.threads_slider = ctk.CTkSlider(
            self.sliders_frame, 
            from_=1, 
            to=10, 
            number_of_steps=9, 
            command=self._update_threads_label
        )
        self.threads_slider.set(3)
        self.threads_slider.pack(pady=(5, 15), padx=15, fill="x")

        # === BUTTONS ===
        self.buttons_frame = ctk.CTkFrame(self, fg_color="transparent")
        self.buttons_frame.pack(pady=15)

        self.start_btn = ctk.CTkButton(
            self.buttons_frame, 
            text="▶️  START BOT", 
            command=self._start_bot,
            width=180, 
            height=50, 
            fg_color="#00cc44", 
            hover_color="#00aa33",
            font=("Segoe UI", 15, "bold")
        )
        self.start_btn.pack(side="left", padx=10)

        self.stop_btn = ctk.CTkButton(
            self.buttons_frame, 
            text="⏹️  STOP", 
            command=self._stop_bot,
            width=180, 
            height=50, 
            fg_color="#cc0000", 
            hover_color="#aa0000",
            font=("Segoe UI", 15, "bold"),
            state="disabled"
        )
        self.stop_btn.pack(side="left", padx=10)

        # === STATS FRAME ===
        self.stats_frame = ctk.CTkFrame(self)
        self.stats_frame.pack(pady=10, padx=25, fill="x")

        self.stats_label = ctk.CTkLabel(
            self.stats_frame, 
            text="📊 Active: 0  |  ✅ Success: 0  |  ❌ Failed: 0", 
            font=("Segoe UI", 13, "bold")
        )
        self.stats_label.pack(pady=10)

        self.progress_bar = ctk.CTkProgressBar(self.stats_frame, mode="determinate", height=15)
        self.progress_bar.set(0)
        self.progress_bar.pack(pady=(5, 15), padx=20, fill="x")

        # === LOG CONSOLE ===
        self.log_textbox = ctk.CTkTextbox(
            self, 
            height=160, 
            activate_scrollbars=True, 
            font=("Consolas", 11),
            fg_color="#1a1a1a"
        )
        self.log_textbox.pack(pady=15, padx=25, fill="both", expand=True)
        self.log("🚀 Viewer Bot initialized. Paste any URL to begin!")

    # === UI CALLBACKS ===
    
    def _toggle_hybrid(self):
        if self.hybrid_switch.get():
            self.proxy_dropdown.configure(state="disabled")
            self.log("🔄 Hybrid Mode: Will rotate through all proxy servers")
        else:
            self.proxy_dropdown.configure(state="normal")
            self.log("📍 Single Server Mode: Using selected proxy only")

    def _update_viewers_label(self, value):
        self.viewers_label.configure(text=f"👥 Viewers: {int(value)}")

    def _update_threads_label(self, value):
        self.threads_label.configure(text=f"⚡ Parallel Threads: {int(value)}")

    def log(self, message):
        """Thread-safe logging"""
        timestamp = datetime.now().strftime("%H:%M:%S")
        
        def _insert():
            self.log_textbox.configure(state="normal")
            self.log_textbox.insert("end", f"[{timestamp}] {message}\n")
            self.log_textbox.see("end")
            self.log_textbox.configure(state="disabled")
        
        if threading.current_thread() is threading.main_thread():
            _insert()
        else:
            self.after(0, _insert)

    def _update_stats(self):
        """Update statistics display"""
        def _update():
            self.stats_label.configure(
                text=f"📊 Active: {self.active_count}  |  ✅ Success: {self.success_count}  |  ❌ Failed: {self.failed_count}"
            )
        self.after(0, _update)

    # === DRIVER CREATION ===
    
    def _create_chrome_driver(self, headless=True, stealth=True):
        """Create an optimized Chrome WebDriver instance"""
        options = webdriver.ChromeOptions()
        
        # Core Settings
        options.add_argument("--log-level=3")
        options.add_argument("--silent")
        options.add_argument("--mute-audio")
        options.add_argument("--no-sandbox")
        options.add_argument("--disable-dev-shm-usage")
        options.add_argument("--disable-infobars")
        options.add_argument("--disable-extensions")
        options.add_argument("--disable-notifications")
        options.add_argument("--disable-popup-blocking")
        
        # Memory Optimization
        options.add_argument("--disable-gpu")
        options.add_argument("--disable-software-rasterizer")
        options.add_argument("--disable-background-timer-throttling")
        options.add_argument("--disable-backgrounding-occluded-windows")
        options.add_argument("--disable-renderer-backgrounding")
        options.add_argument("--disable-features=TranslateUI")
        options.add_argument("--disable-ipc-flooding-protection")
        options.add_argument("--memory-pressure-off")
        options.add_argument("--js-flags=--expose-gc")
        
        # Stealth Settings
        if stealth:
            options.add_argument("--disable-blink-features=AutomationControlled")
            options.add_argument(f"--user-agent={random.choice(self.user_agents)}")
            options.add_experimental_option("excludeSwitches", ["enable-automation", "enable-logging"])
            options.add_experimental_option("useAutomationExtension", False)
        
        # Headless Mode
        if headless:
            options.add_argument("--headless=new")
            options.add_argument("--window-size=1280,720")
        
        # Performance Preferences
        prefs = {
            "profile.managed_default_content_settings.images": 2,  # Disable images
            "profile.default_content_setting_values.notifications": 2,
            "profile.managed_default_content_settings.stylesheets": 2,
            "profile.managed_default_content_settings.fonts": 2,
            "disk-cache-size": 4096,
        }
        options.add_experimental_option("prefs", prefs)
        
        try:
            driver_path = self.driver_manager_path or ChromeDriverManager().install()
            service = Service(driver_path)
            service.log_path = "NUL"  # Suppress driver logs
            
            driver = webdriver.Chrome(service=service, options=options)
            driver.set_page_load_timeout(45)
            driver.implicitly_wait(5)
            
            # Anti-detection script
            if stealth:
                driver.execute_cdp_cmd("Page.addScriptToEvaluateOnNewDocument", {
                    "source": """
                        Object.defineProperty(navigator, 'webdriver', {get: () => undefined});
                        Object.defineProperty(navigator, 'plugins', {get: () => [1, 2, 3, 4, 5]});
                        Object.defineProperty(navigator, 'languages', {get: () => ['en-US', 'en']});
                        window.chrome = {runtime: {}};
                    """
                })
            
            return driver
            
        except Exception as e:
            self.log(f"❌ Driver creation failed: {str(e)[:50]}")
            return None

    # === PROXY INPUT FINDERS ===
    
    def _find_proxy_input(self, driver, timeout=8):
        """Find the URL input field on proxy sites with multiple strategies"""
        selectors = [
            # By ID
            (By.ID, "url"),
            (By.ID, "q"),
            (By.ID, "query"),
            (By.ID, "request"),
            (By.ID, "search"),
            (By.ID, "site-url"),
            (By.ID, "input-url"),
            # By Name
            (By.NAME, "url"),
            (By.NAME, "q"),
            (By.NAME, "query"),
            (By.NAME, "u"),
            # By CSS
            (By.CSS_SELECTOR, "input[type='url']"),
            (By.CSS_SELECTOR, "input[type='text'][name*='url']"),
            (By.CSS_SELECTOR, "input[type='text'][id*='url']"),
            (By.CSS_SELECTOR, "input[placeholder*='url' i]"),
            (By.CSS_SELECTOR, "input[placeholder*='http' i]"),
            (By.CSS_SELECTOR, "input[placeholder*='site' i]"),
            (By.CSS_SELECTOR, "input[placeholder*='address' i]"),
            (By.CSS_SELECTOR, ".input-group input"),
            (By.CSS_SELECTOR, "form input[type='text']"),
            # By XPath
            (By.XPATH, "//input[contains(@placeholder, 'URL')]"),
            (By.XPATH, "//input[contains(@placeholder, 'http')]"),
            (By.XPATH, "//input[contains(@class, 'url')]"),
        ]
        
        for by, value in selectors:
            try:
                element = WebDriverWait(driver, timeout).until(
                    EC.element_to_be_clickable((by, value))
                )
                if element.is_displayed() and element.is_enabled():
                    return element
            except:
                continue
        
        # Last resort: find any visible text input
        try:
            inputs = driver.find_elements(By.CSS_SELECTOR, "input[type='text'], input:not([type])")
            for inp in inputs:
                if inp.is_displayed() and inp.is_enabled():
                    return inp
        except:
            pass
        
        return None

    def _click_submit_buttons(self, driver):
        """Click any go/submit/start buttons on proxy sites"""
        button_keywords = ["go", "start", "browse", "visit", "surf", "continue", 
                          "watch", "enter", "submit", "agree", "accept", "proxy"]
        
        try:
            # Find all clickable elements
            elements = driver.find_elements(By.CSS_SELECTOR, "button, input[type='submit'], input[type='button'], a.btn")
            
            for elem in elements:
                try:
                    text = (elem.text + elem.get_attribute("value") or "").lower()
                    if any(kw in text for kw in button_keywords):
                        if elem.is_displayed() and elem.is_enabled():
                            elem.click()
                            time.sleep(1.5)
                            return True
                except:
                    continue
        except:
            pass
        
        return False

    # === VIEWER LOGIC ===
    
    def _create_single_viewer(self, viewer_id, target_url, proxy_url, headless, stealth):
        """Create a single viewer instance"""
        if not self.is_running:
            return False
        
        driver = None
        retry_count = 0
        max_retries = 2
        
        while retry_count <= max_retries and self.is_running:
            try:
                # Create driver
                driver = self._create_chrome_driver(headless=headless, stealth=stealth)
                if not driver:
                    retry_count += 1
                    continue
                
                with self.lock:
                    self.drivers.append(driver)
                    self.active_count = len(self.drivers)
                self._update_stats()
                
                proxy_name = proxy_url.split("//")[1].split("/")[0]
                self.log(f"🔗 Viewer {viewer_id}: Connecting via {proxy_name}...")
                
                # Load proxy site
                driver.get(proxy_url)
                time.sleep(random.uniform(1.5, 3))
                
                # Find and fill input
                input_field = self._find_proxy_input(driver)
                if not input_field:
                    raise Exception("Input field not found")
                
                # Clear and type URL with human-like delays
                input_field.clear()
                time.sleep(0.3)
                
                # Type URL character by character (more human-like)
                if stealth:
                    for char in target_url:
                        input_field.send_keys(char)
                        time.sleep(random.uniform(0.02, 0.08))
                else:
                    input_field.send_keys(target_url)
                
                time.sleep(0.5)
                input_field.send_keys(Keys.RETURN)
                
                # Wait for navigation
                time.sleep(random.uniform(3, 5))
                
                # Try clicking additional buttons
                self._click_submit_buttons(driver)
                
                # Verify we've navigated somewhere
                time.sleep(2)
                
                self.log(f"✅ Viewer {viewer_id}: Connected successfully!")
                
                with self.lock:
                    self.success_count += 1
                self._update_stats()
                
                return True
                
            except Exception as e:
                retry_count += 1
                error_msg = str(e)[:40]
                
                if retry_count <= max_retries:
                    self.log(f"⚠️ Viewer {viewer_id}: Retry {retry_count}/{max_retries} - {error_msg}")
                    
                    # Cleanup failed driver
                    if driver:
                        try:
                            with self.lock:
                                if driver in self.drivers:
                                    self.drivers.remove(driver)
                            driver.quit()
                        except:
                            pass
                        driver = None
                    
                    time.sleep(1)
                else:
                    self.log(f"❌ Viewer {viewer_id}: Failed - {error_msg}")
                    with self.lock:
                        self.failed_count += 1
                    self._update_stats()
                    return False
        
        return False

    # === BOT CONTROL ===
    
    def _start_bot(self):
        """Start the viewer bot"""
        url = self.url_entry.get().strip()
        
        if not url:
            self.log("❌ Error: Please enter a target URL!")
            return
        
        # Auto-fix URL
        if not url.startswith("http"):
            url = "https://" + url
            self.url_entry.delete(0, "end")
            self.url_entry.insert(0, url)
        
        # Reset counters
        self.is_running = True
        self.success_count = 0
        self.failed_count = 0
        self.active_count = 0
        self.drivers = []
        
        # Update UI
        self.start_btn.configure(state="disabled")
        self.stop_btn.configure(state="normal")
        self.url_entry.configure(state="disabled")
        self.progress_bar.set(0)
        self._update_stats()
        
        # Start bot thread
        threading.Thread(target=self._run_bot, args=(url,), daemon=True).start()

    def _stop_bot(self):
        """Stop all viewers"""
        self.is_running = False
        self.log("🛑 Stopping all viewers...")
        
        # Close all drivers
        for driver in self.drivers[:]:
            try:
                driver.quit()
            except:
                pass
        
        self.drivers.clear()
        self.active_count = 0
        
        # Force garbage collection
        gc.collect()
        
        # Update UI
        self.start_btn.configure(state="normal")
        self.stop_btn.configure(state="disabled")
        self.url_entry.configure(state="normal")
        self._update_stats()
        
        self.log("✅ All viewers stopped and cleaned up")

    def _run_bot(self, target_url):
        """Main bot execution loop"""
        viewer_count = int(self.viewers_slider.get())
        thread_count = int(self.threads_slider.get())
        headless = bool(self.headless_switch.get())
        stealth = bool(self.stealth_switch.get())
        use_hybrid = bool(self.hybrid_switch.get())
        
        self.log(f"🎯 Target: {target_url}")
        self.log(f"📊 Launching {viewer_count} viewers with {thread_count} parallel threads")
        
        # Use ThreadPoolExecutor for parallel creation
        with ThreadPoolExecutor(max_workers=thread_count) as executor:
            futures = []
            
            for i in range(viewer_count):
                if not self.is_running:
                    break
                
                # Select proxy
                if use_hybrid:
                    proxy = self.proxy_urls[i % len(self.proxy_urls)]
                else:
                    proxy = self.proxy_map[self.proxy_dropdown.get()]
                
                # Submit task
                future = executor.submit(
                    self._create_single_viewer,
                    i + 1,
                    target_url,
                    proxy,
                    headless,
                    stealth
                )
                futures.append(future)
                
                # Update progress
                progress = (i + 1) / viewer_count
                self.after(0, lambda p=progress: self.progress_bar.set(p))
                
                # Stagger launches to avoid overwhelming
                time.sleep(random.uniform(0.8, 1.5))
            
            # Wait for all futures to complete
            for future in as_completed(futures):
                try:
                    future.result()
                except:
                    pass
        
        if self.is_running:
            self.log(f"🎉 Complete! {self.success_count}/{viewer_count} viewers active")
            self.progress_bar.set(1)
            
            # Start auto-refresh if enabled
            if self.refresh_switch.get() and self.success_count > 0:
                self.log("🔃 Auto-refresh enabled - viewers will refresh every 5 minutes")
                threading.Thread(target=self._auto_refresh_loop, daemon=True).start()

    def _auto_refresh_loop(self):
        """Periodically refresh active viewers to maintain connection"""
        refresh_interval = 300  # 5 minutes
        
        while self.is_running and self.drivers:
            time.sleep(refresh_interval)
            
            if not self.is_running:
                break
            
            self.log("🔃 Auto-refreshing all active viewers...")
            
            refreshed = 0
            for driver in self.drivers[:]:
                try:
                    driver.refresh()
                    refreshed += 1
                    time.sleep(0.5)
                except:
                    # Driver died, remove it
                    with self.lock:
                        if driver in self.drivers:
                            self.drivers.remove(driver)
                            self.active_count = len(self.drivers)
                    self._update_stats()
            
            self.log(f"✅ Refreshed {refreshed} viewers")

    def on_closing(self):
        """Handle window close event"""
        self.is_running = False
        
        for driver in self.drivers[:]:
            try:
                driver.quit()
            except:
                pass
        
        self.destroy()


if __name__ == "__main__":
    app = ViewerBotApp()
    app.mainloop()