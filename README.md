# JdMegaMind — JD's ARC Skill

Custom ARC (Synthiam) skill for an EZ-Robot JD humanoid — captures
laptop mic audio and JD's camera feed, forwards both to a Python backend
for processing, and executes whatever the backend decides JD should say
and do.

## This skill requires the Python backend to be running

**On its own, this skill does nothing useful.** All speech recognition,
LLM reasoning, face recognition, and text-to-speech happen in a separate
Python/FastAPI backend — see [`JD_Robot_Brain`](https://github.com/Shaheer-Pk/JD_Robot_Brain). This
skill is a thin I/O layer: it grabs mic/camera input, POSTs it to the
backend, and plays back / executes whatever comes back. Without that
backend reachable on the network, buttons in this skill will appear to
do nothing.

**Set up and run the Python backend first**, then come back here.

## Prerequisites

- **Synthiam ARC**, Free tier or higher. **Free tier allows exactly one
  third-party/beta skill per project** — this skill consumes that slot.
  If you already have another third-party skill loaded in your ARC
  project, you'll need to remove it or upgrade to ARC Pro before adding
  this one.
- **Visual Studio Community** — not VS Insiders. ARC's "Create Skill"
  installer does not detect an Insiders install and setup will fail.
- .NET Framework 4.8, x86 (wired automatically when scaffolded through
  ARC)
- An EZ-Robot JD humanoid, connected and configured in ARC with its own
  Camera Device skill already set up (this skill attaches to an
  existing camera skill, it doesn't create one)
- Windows (developed and tested on Windows 11)

## A heads-up on the GUID / cloning this repo

ARC skills are identified in part by a GUID embedded in `plugin.xml`.
This repo's GUID was generated once, on the original development
machine, via ARC's "Create Skill" wizard, and is committed as-is.

**Whether cloning this repo and building it directly on a different
machine loads correctly in ARC has not been tested.** It's expected to
work — the GUID is a static value in source, not something ARC
validates remotely — but this is unverified.

**If ARC fails to load the built DLL as a skill:** the fallback is to
scaffold a brand-new skill via ARC's `Project → Create Skill` wizard on
your own machine (this generates a fresh GUID and a working project
skeleton), then copy this repo's `.cs` source files (`MainForm.cs`,
`AudioBridgeStream.cs`, `CameraFrameUploader.cs`, `ConfigForm.cs`,
`Configuration.cs`) into that fresh skeleton, replacing the
wizard-generated placeholders.

## Setup

1. Set up and confirm the **Python backend** [`Github`](https://github.com/Shaheer-Pk/JD_Robot_Brain) is running
   first.
2. Clone this repo, open the `.sln` in Visual Studio Community.
3. Build.

   **Always close ARC completely before rebuilding.** ARC holds a file
   lock on the loaded DLL — rebuilding while ARC is open produces a
   corrupt/partial DLL that ARC will then refuse to load (`COMException`)
   on next launch. There are no exceptions to this.
4. Load the skill in ARC (via the skill's plugin folder, or through the
   fallback path above if the direct clone doesn't load).

## Configuration — point it at your backend

The skill needs to know where your Python backend is running. Open the
skill's config popup (gear icon in the skill's title bar) and set the
backend address.

**The hostname baked in during development
(`DESKTOP-LJO38UV.local:8000`) is a placeholder specific to the original
dev machine — you must change this to your own backend's address**, not
copy it verbatim. An mDNS `.local` hostname is used in the reference
setup specifically because DHCP/Eduroam-style networks reassign IPs on
reconnect; a plain IP works too if your network is static.

Otherwise execute the bash script in your python backend from the root folder
```bash
uvicorn main:app --reload --host 0.0.0.0
```
with the updated backend address by switching (`DESKTOP-LJO38UV.local:8000`) with the hostname of your system, that can be obtained by executing the PS command

```PowerShell
hostname
```

## Known Limitations

**No HTTPS or API-key authentication on outbound requests.** This
mirrors the same gap on the Python backend side — all requests from this
skill to the backend go out over plain HTTP with no credentials attached.
Only run this on a trusted local network.

---

Backend repo: [`JD_Robot_Brain`](https://github.com/Shaheer-Pk/JD_Robot_Brain)