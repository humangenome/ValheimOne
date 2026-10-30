#!/usr/bin/env python3
"""Browser regressions against an explicitly supplied native fixture."""
import json
import os
from pathlib import Path
import sys
from urllib.parse import urlsplit, parse_qs
from playwright.sync_api import sync_playwright

if os.environ.get('ATLAS_QA_REMOTE') != '1':
    raise SystemExit('Run this check on the authorized remote browser worker.')
base = sys.argv[1].rstrip('/')
out = Path(os.environ.get('ATLAS_QA_ARTIFACT_OUT', 'artifacts/browser-regression'))
out.mkdir(parents=True, exist_ok=True)
results = []

with sync_playwright() as pw:
    browser = pw.chromium.launch(headless=True)
    try:
        for width in (1280, 390):
            context = browser.new_context(viewport={'width': width, 'height': 900})
            context.add_init_script("localStorage.setItem('vo-livemap-layers-v2', JSON.stringify({fog:false}));")
            # Exercise the native HTTP polling fallback so later status changes
            # can be injected without writing server configuration.
            context.add_init_script("window.EventSource = undefined;")
            page = context.new_page()
            errors = []
            page.on('pageerror', lambda error: errors.append(str(error)))
            response = page.goto(base, wait_until='domcontentloaded')
            assert response.status == 200, 'Native map HTML must load'
            version = page.request.get(base + '/api/status').json()['pluginVersion']
            scripts = page.locator('script[src*="/assets/"]').evaluate_all('(els) => els.map(el => el.src)')
            assert scripts and all(parse_qs(urlsplit(src).query).get('v') == [version] for src in scripts), 'Every script URL must select the running release'
            page.wait_for_function("document.querySelector('.fog-overlay')?.naturalWidth > 0", timeout=120000)
            assert page.locator('input[data-layer-key="fog"]').count() == 0, 'Required fog must have no disable control'
            assert page.locator('.fog-overlay').is_visible(), 'Saved fog=false must not disable required fog'
            assert page.locator('.map-cover:not(.is-hidden)').count() == 0, 'Loaded fog must release the loading cover'
            pins = page.request.get(base + '/api/webpins').json()
            assert pins['sharedEditing'] is True, 'Fixture must expose public pins with shared editing enabled'
            assert not page.get_by_role('button', name='Drop a web pin', exact=True).is_visible(), 'Public visitors must not see a pin creation control'
            assert page.evaluate('document.documentElement.scrollWidth <= innerWidth + 1'), 'Page must fit its viewport'
            page.screenshot(path=str(out / f'public-locked-{width}.png'))
            # Only the change notification is simulated below; initial HTML,
            # JavaScript, status, tiles and fog came from the actual native mod.
            locked = [False]
            def status_change(route):
                real = route.fetch()
                payload = real.json()
                payload['map']['fog']['hide'] = locked[0]
                if 'locked' in payload['map']['fog']:
                    payload['map']['fog']['locked'] = locked[0]
                route.fulfill(response=real, json=payload)
            page.route('**/api/status*', status_change)
            page.wait_for_selector('input[data-layer-key="fog"]', state='attached', timeout=30000)
            assert not page.locator('input[data-layer-key="fog"]').is_checked(), 'Unlock restores the saved preference'
            locked[0] = True
            page.wait_for_function("!document.querySelector('input[data-layer-key=fog]')", timeout=30000)
            page.wait_for_function("document.querySelector('.fog-overlay')?.naturalWidth > 0", timeout=30000)
            assert page.locator('.fog-overlay').is_visible(), 'Relocking restores required fog'
            assert errors == [], errors
            results.append({'width': width, 'native_locked_map': 'pass', 'simulated_lock_changes': 'pass'})
            context.close()

        context = browser.new_context(viewport={'width': 1280, 'height': 900})
        page = context.new_page()
        page.route('**/fog.png*', lambda route: route.abort())
        page.goto(base, wait_until='domcontentloaded')
        page.wait_for_selector('.map-cover:not(.is-hidden)', state='attached', timeout=30000)
        page.wait_for_timeout(9000)
        assert page.locator('.map-cover:not(.is-hidden)').is_visible(), 'Failed required fog must remain covered after eight seconds'
        page.screenshot(path=str(out / 'required-fog-network-failure.png'))
        results.append({'injected_fog_network_failure': 'pass'})
        context.close()

        context = browser.new_context(viewport={'width': 1280, 'height': 900})
        page = context.new_page()
        shared = base + '/?token=browser-regression-shared'
        page.goto(shared, wait_until='domcontentloaded')
        page.get_by_role('button', name='Drop a web pin', exact=True).wait_for(state='visible', timeout=30000)
        assert page.locator('input[data-layer-key="fog"]').count() == 0, 'Shared view remains unfogged'
        headers = {'X-LiveMap-Token': 'browser-regression-shared', 'X-Operator': 'BrowserRegression'}
        created = page.request.post(base + '/api/webpins', headers=headers,
            data={'author': 'BrowserRegression', 'icon': 'pin', 'label': 'Temporary browser check', 'x': 0, 'z': 0})
        assert created.ok, created.text()
        pins = page.request.get(base + '/api/webpins', headers=headers).json()['pins']
        pin = next(x for x in pins if x.get('author') == 'BrowserRegression' and x.get('label') == 'Temporary browser check')
        try:
            denied = page.request.post(base + '/api/webpins', data={'icon': 'pin', 'label': 'public write must fail', 'x': 0, 'z': 0})
            assert denied.status == 403, 'Native server must reject public pin writes'
            changed = page.request.patch(base + '/api/webpins/' + pin['id'], headers=headers,
                data={'icon': 'pin', 'label': 'Edited browser check'})
            assert changed.ok, changed.text()
            page.screenshot(path=str(out / 'shared-pin-controls.png'))
        finally:
            deleted = page.request.delete(base + '/api/webpins/' + pin['id'], headers=headers)
            assert deleted.ok, deleted.text()
        pins = page.request.get(base + '/api/webpins', headers=headers).json()['pins']
        assert not any(x['id'] == pin['id'] for x in pins), 'Test pin must be removed'
        results.append({'native_shared_pin_create_edit_delete': 'pass', 'native_public_pin_rejection': 'pass'})
        context.close()

        admin_token = os.environ.get('VALHEIMONE_BROWSER_ADMIN_TOKEN', '')
        if admin_token:
            context = browser.new_context(viewport={'width': 1280, 'height': 900},
                extra_http_headers={'X-LiveMap-Token': admin_token})
            page = context.new_page()
            page.goto(base, wait_until='domcontentloaded')
            page.locator('#console-tab').click()
            # Help is rendered in the browser; this query must reach the native console.
            page.locator('#console-command').fill('banned')
            page.locator('#console-command').press('Escape')
            with page.expect_response(lambda r: '/api/console/exec' in r.url and r.request.method == 'POST') as sent:
                page.locator('#console-command').press('Enter')
            response = sent.value
            assert response.request.post_data_json['command'] == 'banned'
            payload = response.json()
            assert response.ok and payload.get('ok') is True and payload.get('output'), 'Console must execute the submitted command'
            denied = page.request.post(base + '/api/console/exec',
                headers={'X-LiveMap-Token': 'browser-regression-shared'}, data={'command': 'banned'})
            assert denied.status == 401, 'Shared viewers must not execute admin commands'
            page.screenshot(path=str(out / 'admin-command-submission.png'))
            results.append({'native_admin_command_submission': 'pass', 'native_shared_console_rejection': 'pass'})
            context.close()

            # Embedded under a host's own navigation (a hosting panel): the tab the host picks through
            # ValheimOneEmbed.setView stays picked through later statuses. The page around the real embed
            # fragment and scripts is the only stand-in; polling, so statuses arrive on a steady beat.
            context = browser.new_context(viewport={'width': 1280, 'height': 900},
                extra_http_headers={'X-LiveMap-Token': admin_token})
            context.add_init_script("window.EventSource = undefined;")
            page = context.new_page()
            errors = []
            page.on('pageerror', lambda error: errors.append(str(error)))
            fragment = page.request.get(base + '/api/embed').text()
            host = ('<!doctype html><html><head><meta charset="utf-8">'
                '<link rel="stylesheet" href="/assets/leaflet.css"><link rel="stylesheet" href="/assets/app.embed.css">'
                '</head><body>' + fragment +
                '<script>window.VALHEIMONE_EMBED={rootId:"valheimone-embed-root",apiBase:"/",externalNav:true};</script>'
                '<script src="/assets/leaflet.js"></script><script src="/assets/icons.js"></script>'
                '<script src="/assets/app.embed.js"></script></body></html>')
            page.route(base + '/embed-host', lambda route: route.fulfill(status=200, content_type='text/html', body=host))
            def wait_for_statuses(count):
                for _ in range(count):
                    with page.expect_response(lambda r: '/api/status' in r.url, timeout=30000):
                        pass
                    page.wait_for_timeout(200)
            def codex_active():
                return page.evaluate("document.getElementById('valheimone-embed-root').classList.contains('is-codex-active')")
            page.goto(base + '/embed-host', wait_until='domcontentloaded')
            page.wait_for_function('!!window.ValheimOneEmbed', timeout=60000)
            wait_for_statuses(1)
            assert not codex_active(), 'The embed boots on the map'
            page.evaluate("window.ValheimOneEmbed.setView('codex')")
            assert codex_active(), 'The host can pick the Codex'
            wait_for_statuses(3)
            assert codex_active(), 'The Codex the host picked survives later statuses'
            page.evaluate("window.ValheimOneEmbed.setView('map')")
            assert not codex_active(), 'The host can pick the map again'
            wait_for_statuses(2)
            assert not codex_active(), 'The map the host picked survives later statuses'
            assert errors == [], errors
            results.append({'native_embed_host_tab_holds': 'pass'})
            context.close()
    finally:
        browser.close()
        (out / 'result.json').write_text(json.dumps(results, indent=2))
print(json.dumps(results))
