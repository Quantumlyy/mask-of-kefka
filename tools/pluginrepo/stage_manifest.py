# Copies the packager's plugin manifest into the plugin repository, recording the repository whose
# GitHub releases serve its zip. Usage: stage_manifest.py <source.json> <dest.json> <owner/repo>
import json
import sys


def main(source, dest, releases_repo):
    with open(source, encoding='utf-8') as f:
        manifest = json.load(f)
    manifest['ReleasesRepo'] = releases_repo
    with open(dest, 'w', encoding='utf-8') as f:
        json.dump(manifest, f, indent=2, ensure_ascii=False)
        f.write('\n')


if __name__ == '__main__':
    main(*sys.argv[1:4])
