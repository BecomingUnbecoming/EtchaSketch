# Contributing

## Keep personal details out of the repository

This repository is public. Everything in it is public forever, including files, commit messages,
commit author details, pull request titles, bodies and comments, issue text, CI logs and release
notes. Editing or deleting something later does not reliably remove it, so it must never go in.

**Never put any of these in the repository or on GitHub:**

- Real names of the people who use or build the app, or of their friends and family. Examples, tests
  and screenshots use made-up names (the tests use Robin and Kai).
- Real addresses on anyone's network: IP addresses, host names, machine or VM names, internal domain
  names, ports of a particular home setup. Examples use `192.168.1.20`, `sync.home.example` or
  `localhost`.
- Paths on someone's computer (`/home/<name>/…`), device names, or which password manager, backup or
  storage someone uses and where.
- Secrets of any kind: tokens, passwords, keys, keystores, recovery keys, pairing codes, API keys,
  `.env` files with real values. Secrets go in GitHub Actions secrets or a password manager, and
  examples use obvious placeholders such as `change-me`.
- Real data from anyone's app: task titles, notes, chores, pantry items, calendar events, exports or
  imports. Test data is made up.
- Personal email addresses. Commits use your GitHub `noreply` address (Settings → Emails → "Keep my
  email addresses private").

**Fine to include:** a certificate *fingerprint* (it is public by design and lets people verify
builds), the names of GitHub secrets (not their values), and the general shape of a setup ("a small
VM on a home server").

When a pull request needs details of a specific setup to be tested, describe the setup in general
terms and keep the specifics in your own notes.

## How we work

- One branch and one pull request per step of the development plan, always targeting `main`.
- Pull request descriptions are written in plain language: what changes for someone using the app,
  where to look in the code, and what the tests don't cover.
- Run the full test suite before pushing: `dotnet test tests/EtchaSketch.Tests`.
- Anything touching privacy tiers, the privacy gateway, stream mode or publishing comes with a test
  that a Private field cannot leak.
