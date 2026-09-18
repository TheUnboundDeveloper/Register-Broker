# Privacy Policy

**Product:** Register Broker — Universal Low-Level Hardware Access Framework
(kernel driver `BrokerSmbus`, the `SensorBroker` and `BrokerControl` services, the
Reference Console, the RGB Audio Reactive tool, and the installer that packages them).

**Publisher:** nvaitehiarna ("TheUnboundDeveloper")
**Contact:** TheUnboundDeveloper@outlook.com
**Effective:** 2026-09-18 · **Applies to:** Register Broker 1.6.0 and later

---

## The short version

**Register Broker collects nothing, transmits nothing, and contains no network code.**

There are no accounts, no sign-in, no telemetry, no analytics, no crash reporting, no
advertising, no update check, and no third-party SDKs. The software has no HTTP client,
no socket, and no server it talks to. Nothing it reads about your computer or your
hardware ever leaves your computer.

This restates, in formal terms, the disclaimer shown in the application itself:

> No data is ever mined, recorded, seen, researched, or distributed as part of this
> project. All hardware access stays local to your machine.

---

## What the software reads

Register Broker exists to read hardware sensors and drive RGB lighting. To do that it
reads, **in memory, on your machine only**:

- Sensor telemetry — CPU and chipset temperatures, DIMM temperatures, fan speeds and
  PWM duty, voltages, and GPU/UPS/Aquacomputer readings when those optional backends
  are enabled.
- Hardware identity needed to pick the right register map — your motherboard's DMI
  strings (manufacturer, product name), CPU family/model, and the vendor/product IDs
  and model strings of attached RGB and USB-HID devices.
- The identity of local programs that connect to it — see *Audit log*, below.

This information is served to local client applications over two fixed, DACL-protected
Windows named pipes (`\\.\pipe\SensorBroker`, `\\.\pipe\BrokerControl`). Named pipes in
this configuration are local-only; the software never opens a network listener and never
initiates an outbound connection.

None of this is personal data in the ordinary sense, and none of it is transmitted,
aggregated, sold, shared, or seen by the publisher.

## What the software writes to your disk

Everything below stays on your machine. Nothing is uploaded. You may delete any of it at
any time; the software recreates only what it needs.

| Location | Contents | Retention |
|---|---|---|
| `%LOCALAPPDATA%\BrokerSensorBridge\bridge.log` | Diagnostic service log: startup, detected hardware backends, errors. For the LocalSystem services this resolves to `C:\Windows\System32\config\systemprofile\AppData\Local\`. | Capped at 5 MB, then rolled to `.1`; one rollover kept |
| `%LOCALAPPDATA%\BrokerSensorBridge\audit.log` | Control-plane audit trail (see below) | Capped at 5 MB, then rolled to `.1`; one rollover kept |
| `%APPDATA%\RegisterBroker\ReferenceConsole\settings.json` | Reference Console preferences only — dashboard layout, your custom sensor/device labels, window state | Until you delete it |
| `C:\ProgramData\SensorBroker\calibration.user.json` | Optional, user-created sensor relabel/rescale overrides | Until you delete it |
| `C:\Program Files\Register Broker\setup\*.log` | Installer logs for the kernel-driver and configuration steps | Until uninstall |
| `HKLM\SOFTWARE\RegisterBroker\DriverStatus` | One value recording whether the kernel service installed and started | Removed on uninstall |

### Audit log

The broker is a security-posture product: it records what asked it to do what. Each
control-plane event — connection, authorization decision, operation name and result,
rate-limit or session-limit rejection — is appended to `audit.log` together with the
requesting program's **executable path** and, where the program is signed, its
**Authenticode signer subject**.

That is deliberate and is the point of an audit trail, but it is worth naming plainly:
if you run a program from a path containing your Windows user name, that path appears in
a local log file. The log is written under the service account's profile, is readable
only by administrators, is size-capped, and is never transmitted anywhere.

## What the kernel driver does not do

The `BrokerSmbus` driver exposes a narrow, fixed set of bounded IOCTLs that address
named hardware registers on allow-listed buses. It does **not** read arbitrary physical
memory, MSRs, process memory, files, the clipboard, the screen, keystrokes, or network
traffic, and it has no code path that could transmit anything.

## Contacting the developer

The Reference Console's **✉ Send feedback** button opens *your own* mail client with a
pre-filled subject line. It sends nothing by itself — no message leaves your machine
unless you write one and press send in your mail program.

If you do email TheUnboundDeveloper@outlook.com, that correspondence (your email address
and whatever you choose to include, such as logs or hardware details you attach) is
received through the Outlook.com mail service and is used **only** to answer you and to
fix the problem you reported. It is never sold, shared, or used for marketing, and it is
not added to any mailing list. Ask at any time and your correspondence will be deleted.

## Third-party components

Register Broker ships no third-party analytics, advertising, or data-collection library.
At runtime it may load hardware-vendor libraries that are already present on your system
— AMD's `atiadlxx.dll`, NVIDIA's `nvml.dll`, Intel's `ze_loader.dll`, and the Windows
`hid.dll` / `setupapi.dll` — solely to read sensor values through their documented
read-only interfaces. Register Broker passes no data to them beyond the query itself.

Independently of this software, Windows may send crash data to Microsoft through Windows
Error Reporting if any program on your system faults. That behaviour belongs to Windows
and is governed by Microsoft's privacy statement and your own Windows diagnostic-data
settings, not by this policy.

## Children

Register Broker is a system utility for PC hardware. It is not directed at children, and
because it collects no data at all, it collects no data from children.

## Your rights (GDPR, UK GDPR, CCPA/CPRA and similar)

The publisher operates no server and holds no database, so there is no stored profile,
sale, or sharing of personal information to disclose, and no cross-context behavioural
advertising of any kind.

- **Access / portability / erasure:** all data the software produces is already in your
  possession, in the files listed above. Delete them and it is gone.
- **Opt out of sale or sharing:** nothing is ever sold or shared; there is nothing to
  opt out of.
- **Email correspondence:** for anything you have emailed the publisher, write to
  TheUnboundDeveloper@outlook.com to request a copy or its deletion.

## How to verify all of this

Register Broker is published under AGPL-3.0 with a Commercial Exception, so every claim
above is checkable rather than merely asserted:

- The complete source is at <https://github.com/TheUnboundDeveloper/Register-Broker>.
- There is no networking code to find: search the tree for `HttpClient`, `WebRequest`,
  or `Socket` and you will get no hits outside of comments.
- Watch it live — the services' behaviour is visible in `bridge.log` and `audit.log`,
  and any network-monitoring tool will show the process making no connections.

## Changes to this policy

Material changes are recorded in this file's git history and reflected in the effective
date above. Because this policy is versioned alongside the source, you can diff any two
revisions of it.

## Contact

Privacy questions, or anything in this document that looks wrong:
**TheUnboundDeveloper@outlook.com**

Security vulnerabilities go through the process in [SECURITY.md](../SECURITY.md)
(private GitHub advisory preferred) rather than this address.
