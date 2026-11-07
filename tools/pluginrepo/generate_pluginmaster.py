# Builds pluginmaster.json for the Dalamud plugin repository from plugins/<name>/<name>.json.
# Run inside the plugin repository by the plugin's build-plugin workflow, which also copies this file
# there. Based on anomek/MyDalamudPlugins, which reworked Aireil's generator to build download links
# from GitHub Releases (so downloads are counted) instead of committed zips.
#
# A manifest's ReleasesRepo (owner/repo, set by stage_manifest.py) names the repository whose
# releases serve latest.zip; RepoUrl stays the project page and is only a fallback.

import json
import os
import re
import urllib.request
import urllib.error
from datetime import datetime, timezone
from time import time

TOKEN = os.environ.get('GITHUB_TOKEN', '')

RELEASE_ASSET = 'latest.zip'
# Stable URL that always redirects to the newest release's asset.
DOWNLOAD_URL = 'https://github.com/{owner}/{repo}/releases/latest/download/' + RELEASE_ASSET

DEFAULTS = {
    'IsHide': False,
    'IsTestingExclusive': False,
    'ApplicableVersion': 'any',
}

DUPLICATES = {
    'DownloadLinkInstall': ['DownloadLinkTesting', 'DownloadLinkUpdate'],
}

TRIMMED_KEYS = [
    'Author',
    'Name',
    'Punchline',
    'Description',
    'Changelog',
    'InternalName',
    'AssemblyVersion',
    'MinimumDalamudVersion',
    'RepoUrl',
    'ApplicableVersion',
    'Tags',
    'DalamudApiLevel',
    'IconUrl',
    'ImageUrls',
]


def main():
    manifests = load_manifests()
    master = [trim_manifest(m) for m in manifests]
    add_extra_fields(master, manifests)
    write_master(master)


def load_manifests():
    manifests = []
    if not os.path.isdir('plugins'):
        return manifests
    for name in sorted(os.listdir('plugins')):
        path = os.path.join('plugins', name, f'{name}.json')
        if not os.path.isfile(path):
            continue
        with open(path, encoding='utf-8') as f:
            manifests.append(json.load(f))
    return manifests


def trim_manifest(plugin):
    return {k: plugin[k] for k in TRIMMED_KEYS if k in plugin}


def parse_repo(repo_url):
    m = re.search(r'github\.com[:/]+([^/]+)/([^/]+?)(?:\.git)?/?$', (repo_url or '').strip())
    if not m:
        return None, None
    return m.group(1), m.group(2)


def releases_repo(manifest):
    if '/' in (manifest.get('ReleasesRepo') or ''):
        return tuple(manifest['ReleasesRepo'].split('/', 1))
    return parse_repo(manifest.get('RepoUrl'))


def api_get(url):
    req = urllib.request.Request(url)
    req.add_header('Accept', 'application/vnd.github+json')
    req.add_header('X-GitHub-Api-Version', '2022-11-28')
    if TOKEN:
        req.add_header('Authorization', f'Bearer {TOKEN}')
    with urllib.request.urlopen(req) as resp:
        return json.loads(resp.read().decode('utf-8'))


def fetch_releases(owner, repo):
    releases, page = [], 1
    while True:
        try:
            batch = api_get(
                f'https://api.github.com/repos/{owner}/{repo}/releases?per_page=100&page={page}'
            )
        except (urllib.error.HTTPError, urllib.error.URLError) as e:
            print(f'  ! releases fetch failed for {owner}/{repo}: {e}')
            break
        if not batch:
            break
        releases.extend(batch)
        if len(batch) < 100:
            break
        page += 1
    return releases


def release_stats(owner, repo):
    """Returns (download_count, last_update_epoch).
    download_count = MAX asset count across releases (not sum): with auto-update every
    version re-downloads the whole active base, so the peak single release ~= real users."""
    max_count, last_update = 0, 0
    for rel in fetch_releases(owner, repo):
        for asset in rel.get('assets', []):
            if asset.get('name') == RELEASE_ASSET:
                max_count = max(max_count, int(asset.get('download_count', 0)))
        published = rel.get('published_at')
        if published:
            try:
                ts = int(datetime.strptime(published, '%Y-%m-%dT%H:%M:%SZ')
                         .replace(tzinfo=timezone.utc).timestamp())
                last_update = max(last_update, ts)
            except ValueError:
                pass
    return max_count, last_update


def add_extra_fields(master, manifests):
    for manifest, source in zip(master, manifests):
        owner, repo = releases_repo(source)
        if owner and repo:
            manifest['DownloadLinkInstall'] = DOWNLOAD_URL.format(owner=owner, repo=repo)
            count, last_update = release_stats(owner, repo)
            print(f'{manifest.get("InternalName")}: releases from {owner}/{repo}, max downloads={count}')
        else:
            count, last_update = 0, 0
            print(f'{manifest.get("InternalName")}: no ReleasesRepo and no parsable RepoUrl, skipping releases')

        for k, v in DEFAULTS.items():
            manifest.setdefault(k, v)
        for key, copies in DUPLICATES.items():
            if key not in manifest:
                continue
            for k in copies:
                manifest.setdefault(k, manifest[key])

        manifest['DownloadCount'] = count
        manifest['LastUpdate'] = str(last_update or int(time()))


def write_master(master):
    with open('pluginmaster.json', 'w', encoding='utf-8') as f:
        json.dump(master, f, indent=4, ensure_ascii=False)
        f.write('\n')


if __name__ == '__main__':
    main()
