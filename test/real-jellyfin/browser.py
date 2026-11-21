"""Headless Chromium logs in and checks the Home page. Run by run.sh inside the Playwright image."""

import sys

from playwright.sync_api import sync_playwright

base = sys.argv[1]
out = sys.argv[2]
bad = False
with sync_playwright() as p:
    browser = p.chromium.launch()
    for name, size in (
        ("desktop", {"width": 1280, "height": 800}),
        ("phone", {"width": 390, "height": 800}),
    ):
        page = browser.new_context(viewport=size).new_page()
        page.goto(base + "/web/index.html", wait_until="networkidle", timeout=60000)
        page.wait_for_selector("#txtManualName", timeout=30000)
        page.fill("#txtManualName", "admin")
        page.fill("#txtManualPassword", "lf-test-pass")
        page.click("button[type=submit]")
        page.wait_for_timeout(9000)
        rows = page.evaluate(
            "[...document.querySelectorAll('.lshelf-row h2, .lshelf-row .lshelf-title')].map(e => e.textContent.trim())"
        )
        accent = page.evaluate(
            "getComputedStyle(document.documentElement).getPropertyValue('--lshelf-accent')"
        ).strip()
        ok = rows == ["New this week", "Leaving soon"] and accent == "#e50914"
        print(
            ("[ ok ] " if ok else "[FAIL] ")
            + f"{name}: rows {rows}, accent {accent or 'none'}"
        )
        bad = bad or not ok
        page.screenshot(path=f"{out}/home-{name}.png")
    browser.close()
sys.exit(1 if bad else 0)
