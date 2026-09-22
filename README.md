# Level Play Helper

[![Unity](https://img.shields.io/badge/Unity-2022.3%2B-black?logo=unity)](https://unity.com)
[![LevelPlay SDK](https://img.shields.io/badge/Ads%20Mediation-9.5.0%2B-blue)](https://docs.unity.com/en-us/grow/levelplay/)
[![License: MIT](https://img.shields.io/badge/License-MIT-green.svg)](LICENSE)
[![Install](https://img.shields.io/badge/install-Git%20URL-orange)](#installation-unity-package-manager---git-url)

A drop-in, production-ready manager for **Unity LevelPlay (Ads Mediation)**. Add one component, fill in your keys, and get SDK initialization, GDPR/CCPA/COPPA consent, interstitial/rewarded/banner lifecycle with exponential-backoff retries, impression-level revenue events, an **in-editor Setup and Config window** (Credentials, Cloud API and Checklist tabs), and a documented custom inspector — all without writing any mediation boilerplate in your game code.

Built to be reused as-is across projects: subclass it for game-specific ad cadence, or use it directly.

## Table of Contents

- [Why this package](#why-this-package)
- [Installation](#installation-unity-package-manager---git-url)
- [Quick Start](#quick-start)
- [Setup and Config (Editor Tool)](#setup-and-config-editor-tool)
- [Custom Inspector](#custom-inspector)
- [Configuration Reference](#configuration-reference)
- [API Reference](#api-reference)
- [Events](#events)
- [Fallback Behavior](#fallback-behavior-rewarded-ads)
- [Privacy & Consent (GDPR / CCPA / COPPA)](#privacy--consent-gdpr--ccpa--coppa)
- [Impression-Level Revenue (ILRD) → Analytics](#impression-level-revenue-ilrd--analytics)
- [Extending: Game-Specific Subclass](#extending-game-specific-subclass)
- [Banner Positioning](#banner-positioning)
- [Runtime Debug Overlay](#runtime-debug-overlay)
- [Testing](#testing)
- [Production Release Checklist](#production-release-checklist)
- [Auto-Update Notifications](#auto-update-notifications)
- [Architecture](#architecture)
- [Troubleshooting / FAQ](#troubleshooting--faq)
- [Versioning & Releases](#versioning--releases)
- [Contributing](#contributing)
- [License](#license)

## Why this package

Integrating LevelPlay directly usually means copy-pasting the same ~500 lines of init/retry/callback plumbing into every project, and re-solving the same edge cases each time: what happens when a rewarded ad isn't ready when the player taps the button? Do you remember to unsubscribe every event on destroy? Did you disable the Test Suite flag before shipping?

`LevelPlayHelper` centralizes all of that once, tested, and versioned — so each game only adds the parts that are actually game-specific (ad cadence, UI, save-data hooks) on top of it.

## Installation (Unity Package Manager - Git URL)

1. Open **Window > Package Manager**
2. Click **+** > **Add package from git URL...**
3. Paste:

```
https://github.com/wagenheimer/UnityLevelPlayHelper.git
```

The [Unity Ads Mediation package](https://docs.unity.com/en-us/grow/levelplay/) (`com.unity.services.levelplay`) is installed automatically as a dependency — you don't need to install it separately.

To pin a specific released version instead of `master`, append `#vX.Y.Z`:

```
https://github.com/wagenheimer/UnityLevelPlayHelper.git#v1.2.0
```

### Requirements

| Requirement | Version |
|---|---|
| Unity | 2022.3 LTS or newer |
| Ads Mediation package (`com.unity.services.levelplay`) | 9.5.0+ (auto-installed) |
| Native dependency resolver | EDM4U or Mobile Dependency Resolver (ships with the Ads Mediation package) |

Native dependency resolution is required before building to device: **Assets > External Dependency Manager > Android Resolver > Resolve** for Android, and **iOS Resolver > Install Cocoapods** for iOS. The [Setup and Config](#setup-and-config-editor-tool) verifies this for you.

## Quick Start

1. Add the `LevelPlayHelper` component (or your own subclass — see [Extending](#extending-game-specific-subclass)) to a persistent GameObject in your first scene.
2. In the Inspector, fill in the **App Key** from the [LevelPlay dashboard](https://platform.ironsrc.com/) for Android and/or iOS.
3. Fill in the Ad Unit IDs per platform. Leave an ID **empty** to disable that specific format on that platform — for example, leave Banner IDs empty if you don't use banners.
4. (Optional) Adjust **Consent Settings** for GDPR / CCPA / COPPA — see [Privacy & Consent](#privacy--consent-gdpr--ccpa--coppa).
5. Run **Tools > Wagenheimer > Level Play Helper > Setup & Config...** to verify nothing is missing before you build.
6. Press Play — mock ads load automatically in the Editor, no extra setup needed.

```csharp
// Show an interstitial at a natural break point
LevelPlayHelper.Instance.ShowInterstitial();

// Show a rewarded ad, granting the reward only when actually earned
LevelPlayHelper.Instance.ShowRewardedAd(() =>
{
    player.AddCoins(50);
});
```

That's the entire integration surface for most games.

## Setup and Config (Editor Tool)

The package ships a single **EditorWindow** built with UI Toolkit. It is the one place to **configure** and **verify** everything, so you don't have to manually re-verify the [LevelPlay integration guide](https://docs.unity.com/en-us/grow/levelplay/) each release.

**Open it via:**
- `Tools > Wagenheimer > Level Play Helper > Setup & Config...`, or
- The **Open Setup & Config** button at the top of the `LevelPlayHelper` component's Inspector.

The window has three tabs:

| Tab | Purpose |
|---|---|
| **Credentials** | Edits the App Key + Ad Unit IDs per platform **directly on the helper prefab**, with live validation (valid / empty / placeholder / malformed) and a **Check credentials** button that prints a per-field summary. |
| **Cloud (API)** | Talks to the ironSource / LevelPlay publisher API: connect, fetch your applications and ad units, **apply** them to the helper prefab, create an application, create missing ad units, and enable the default networks (ironSource + UnityAds). |
| **Checklist** | The full scan below: eight collapsible sections of automated checks with one-click fixes. |

### Cloud (API) tab

Uses your account **Secret Key** and **Refresh Token** (LevelPlay > My Account > API) to read and write the account:

- **Connect** – exchanges them for a bearer token (cached for its 24h lifetime).
- **Fetch applications** – lists your apps, matched to the project by bundle id per platform, and shows the **active networks per format**.
- **Apply App Keys / Apply Ad Unit IDs** – writes the values into the helper prefab (no copy-paste).
- **Create application** – shown only when the account has no app yet; supports an app that is not published (name + platform) or one already on the store (store URL + taxonomy).
- **Create missing ad units** – creates the formats that do not exist yet.
- **Enable default networks** – activates the existing per-unit default instance and creates what is missing (ironSource + UnityAds).

Every dashboard write asks for confirmation first. The credentials are stored in **EditorPrefs on your machine only** - they are never written to the project, logged, or committed. The **Checklist** tab then adds a **Dashboard cross-check** that flags an App Key that is not an app on the account, an Ad Unit ID that does not belong to the app, a **paused** ad unit, a format used with the wrong ID, or an ad unit with **no active network** - exactly the causes of a runtime `invalid ad unit id` or of ads that never fill.

The Checklist tab groups the checks into eight collapsible sections. Each section shows a progress chip; each row shows a colour-coded status glyph, an explanation, an expandable **details** list (paths, detected values, resolved versions) and a **Docs** button.

| Section | What it verifies |
|---|---|
| 1 · Package & SDK | Ads Mediation installed and its resolved version (≥ 9.4.0 for the privacy APIs, ≥ 9.5.0 for per-instance ILRD), mediation adapters present, no legacy `Assets/IronSource` copy, Android dependencies served from Maven Central, SDK auto-init not fighting your own init call, `EnableAdapterDebug` / `EnableIntegrationHelper` off |
| 2 · Native dependencies | EDM4U / UEDM / MDR installed, Android resolve recorded (mediation SDK + adapter + `play-services-ads-identifier`), adapter dependency descriptors present, Gradle template state, iOS CocoaPods, Android `INTERNET` permission |
| 3 · Helper component | Instance found (with asset/scene paths), reachable at runtime (first enabled build scene, or a prefab under a `Resources` folder), single instance (singleton hygiene) |
| 4 · Configuration | App Key per platform with placeholder detection, the full Ad Unit ID matrix (3 formats × 2 platforms), every format the project actually calls has IDs, credential shape validation, **dashboard cross-check** (IDs belong to the app, not paused, format matches, active network present) when the Cloud tab has fetched the account, consent flags, ad cadence sanity, Test Suite flag |
| 5 · Android build | IL2CPP backend, ARM64 in Target Architectures, `AD_ID` permission for API 33+ (custom manifest **or** the SDK's `DeclareAD_IDPermission` **or** the merged `play-services-ads-identifier` AAR), reported min SDK |
| 6 · iOS build | App Tracking Transparency implemented, `NSUserTrackingUsageDescription` written by a post-build step, SKAdNetwork automation, AdMob app IDs when AdMob mediation is enabled |
| 7 · Project code integration | Scans your sources (never packages) for rewarded/interstitial/banner usage and readiness guards, interstitial pacing, ILRD consumer, direct privacy API usage, bid floors, and deprecated/removed symbols (`IronSource.Agent`, `com.unity3d.mediation`, `OnImpressionDataReadyEvent`, `SetConsent`, `do_not_sell`, `is_child_directed`, `onApplicationPause`) |
| 8 · Release validation | The manual items from the production checklist: dashboard app/ad units with active network instances, credentials matching the dashboard, Test Suite validated on a physical device, Development Build while testing, multi-device testing, airplane-mode error handling, iOS privacy manifest, mock-ad limitations |

Status legend: **✓ pass**, **! warning**, **✕ fail**, **• manual**, **i info**. The header shows the summary (automated checks passed, fail and warning counts) with a progress bar tinted by the worst status, plus the detected LevelPlay SDK and helper versions. Click **Refresh** after making changes to re-run every check.

```
┌──────────────────────────────────────────────────────────────┐
│  LevelPlay - Setup & Config       [Dashboard][Docs][Refresh]  │
│  LevelPlay SDK 9.5.1 | helper 1.2.0 | checked 14:32:07        │
│  ✓ All automated checks passed        [24/26 automated]       │
│  ████████████████████████████████░░                           │
├──────────────────────────────────────────────────────────────┤
│  v 1 - Package & SDK                        [7/7]             │
│    ✓  Ads Mediation package installed                         │
│       Unity.Services.LevelPlay assembly resolved.             │
│       details (2)                                             │
│    !  SDK version meets the privacy / ILRD minimums [Docs]    │
│       LevelPlay 9.4.x: ILRD still uses the global event.      │
├──────────────────────────────────────────────────────────────┤
│  v 8 - Release validation                 [manual]            │
│    •  Test Suite validated on a real device           [Docs]  │
│       Mock ads never exercise load/display failures.          │
└──────────────────────────────────────────────────────────────┘
```

## Custom Inspector

Selecting a `LevelPlayHelper` (or subclass) shows a custom-drawn Inspector instead of the raw default one:

- A help box explaining the component and how empty Ad Unit ID fields behave
- Quick buttons: **Open Setup & Config**, **Dashboard**, **Docs**
- A live **Runtime Status** block while in Play Mode: SDK initialized, interstitial ready, rewarded ready
- All the normal serialized fields below, unmodified
- Footer links to the README, changelog, and issue tracker

## Configuration Reference

All fields live under these Inspector headers on the `LevelPlayHelper` component:

| Header | Field | Type | Default | Notes |
|---|---|---|---|---|
| App Key | `androidAppKey` / `iosAppKey` | string | `""` | From the LevelPlay dashboard, per platform. |
| Ad Unit IDs - Android | `androidInterstitialAdUnitId` / `androidRewardedAdUnitId` / `androidBannerAdUnitId` | string | `""` | Empty = format disabled on Android. |
| Ad Unit IDs - iOS | `iosInterstitialAdUnitId` / `iosRewardedAdUnitId` / `iosBannerAdUnitId` | string | `""` | Empty = format disabled on iOS. |
| Banner | `bannerPosition` | `BannerPositionPreset` | `BottomCenter` | One of the 9 standard LevelPlay banner positions. |
| Settings | `adsConfig` (`AdsConfiguration`) | object | see below | Reserved for cadence logic in subclasses (see [Extending](#extending-game-specific-subclass)); the base class itself doesn't gate ad calls on it. |
| Settings | `consentConfig` (`ConsentConfiguration`) | object | see below | GDPR/CCPA/COPPA flags — see [Privacy & Consent](#privacy--consent-gdpr--ccpa--coppa). |
| Testing | `enableTestSuite` | bool | `false` | Launches the [Test Suite](#testing) after init on device builds. **Disable before release.** |

`AdsConfiguration`:

| Field | Default | Meaning |
|---|---|---|
| `minAdInterval` | 2 | Minimum number of checks between ads (for subclass cadence logic). |
| `initialAdInterval` | 5 | Initial check interval before showing ads. |
| `adsNeededToReduceInterval` | 3 | Ads shown needed to reduce the interval. |

`ConsentConfiguration`:

| Field | Default | Meaning |
|---|---|---|
| `enableGDPRConsent` | `true` | Applies stored GDPR consent to the SDK before `Init()`. |
| `ccpaOptOut` | `false` | User opted out of data sale (CCPA/CPRA). |
| `coppaChildDirected` | `false` | Marks the app as child-directed (COPPA). |

## API Reference

### Lifecycle

| Member | Signature | Description |
|---|---|---|
| `Instance` | `static LevelPlayHelper Instance { get; }` | Singleton set in `Awake()`. Persists via `DontDestroyOnLoad`. |
| `Initialize()` | `void Initialize()` | Applies privacy settings and calls `LevelPlay.Init()`. Called automatically from `Start()`; safe to call again (no-op after first success). |
| `IsSdkInitialized` | `bool IsSdkInitialized { get; }` | True once `OnInitSuccess` has fired. |
| `SetUserConsent(bool)` | `void SetUserConsent(bool hasConsent)` | Persists consent to `PlayerPrefs` and re-applies GDPR consent immediately. Call **before** `Initialize()` for it to affect this session's init. |

### Interstitial & Rewarded

| Member | Signature | Description |
|---|---|---|
| `IsInterstitialReady()` | `bool IsInterstitialReady()` | True if an interstitial is loaded and ready to show. |
| `ShowInterstitial()` | `void ShowInterstitial()` | Shows the interstitial if ready; otherwise logs a warning and triggers a reload. |
| `IsRewardedAdReady()` | `bool IsRewardedAdReady()` | True if a rewarded ad is loaded and ready to show. |
| `ShowRewardedAd(Action)` | `void ShowRewardedAd(Action onReward)` | Shows a rewarded ad; invokes `onReward` only when the reward is actually earned. See [Fallback Behavior](#fallback-behavior-rewarded-ads). |
| `TryShowAd(Action)` | `bool TryShowAd(Action onRewardGranted = null)` | Shows whichever of rewarded/interstitial is ready, prioritizing rewarded. Returns `false` if nothing is available. |

### Banner

| Member | Signature | Description |
|---|---|---|
| `CreateBanner()` | `void CreateBanner()` | Creates and loads the banner using the configured position. No-op if already created or no banner Ad Unit ID is set. |
| `ShowBanner()` | `void ShowBanner()` | Shows the banner, creating it first if needed. |
| `HideBanner()` | `void HideBanner()` | Hides the banner without destroying it. |
| `DestroyBanner()` | `void DestroyBanner()` | Unsubscribes events and destroys the banner ad object. Called automatically on `OnDestroy()`. |

### Exposed Configuration

| Member | Signature | Description |
|---|---|---|
| `AdsConfig` | `AdsConfiguration AdsConfig { get; }` | Read-only accessor for the cadence configuration block. |
| `ConsentConfig` | `ConsentConfiguration ConsentConfig { get; }` | Read-only accessor for the consent configuration block. |

## Events

All events are **static**, so you can subscribe from anywhere without holding a reference to the instance.

```csharp
LevelPlayHelper.OnSdkInitialized      += config => { };
LevelPlayHelper.OnInterstitialClosed  += () => { };
LevelPlayHelper.OnRewardedAdGranted   += () => { };
LevelPlayHelper.OnAdRevenuePaid       += (adUnitId, revenue) => { /* analytics */ };

// Raw impression-level revenue data (fires on a background thread!)
LevelPlayHelper.OnImpressionDataReady += data => { };
```

| Event | Signature | Fires when |
|---|---|---|
| `OnSdkInitialized` | `Action<LevelPlayConfiguration>` | `LevelPlay.Init()` succeeds. |
| `OnInterstitialClosed` | `Action` | An interstitial is dismissed. |
| `OnRewardedAdGranted` | `Action` | A rewarded ad grants its reward. |
| `OnAdRevenuePaid` | `Action<string, double>` | Every paid impression — mediation Ad Unit ID + estimated USD revenue. Convenience wrapper over `OnImpressionDataReady`. |
| `OnImpressionDataReady` | `Action<LevelPlayImpressionData>` | Every paid impression, raw SDK payload. **Background thread** — dispatch to the main thread yourself if touching Unity APIs. |

Remember to unsubscribe in `OnDestroy()` on any listener with a shorter lifetime than the app.

## Fallback Behavior (Rewarded Ads)

`ShowRewardedAd` never blocks the player waiting on ad inventory:

1. **Rewarded ad is ready** → show it and grant the reward through the callback when earned.
2. **Otherwise, interstitial is ready** → show the interstitial and grant the reward immediately (no rewarded ad = no interruption to the player's expected flow).
3. **Nothing available** → grant the reward directly, no ad shown.

In all three cases, a rewarded reload is triggered afterward so the next request is more likely to have inventory.

## Privacy & Consent (GDPR / CCPA / COPPA)

`ConsentConfiguration` on the component controls what's applied automatically before `LevelPlay.Init()`:

- **GDPR** (`enableGDPRConsent`, default `true`): reads consent from `PlayerPrefs` key `UserConsent` (`1` = consented, anything else = not consented) and calls `LevelPlayPrivacySettings.SetGDPRConsent(...)`.
- **CCPA** (`ccpaOptOut`): if `true`, calls `LevelPlayPrivacySettings.SetCCPA(true)` — user opted out of data sale.
- **COPPA** (`coppaChildDirected`): if `true`, calls `LevelPlayPrivacySettings.SetCOPPA(true)` — marks the app as child-directed.

If you run your own consent management platform (CMP) UI, call `SetUserConsent(bool)` as soon as the player answers, **before** `Initialize()` runs (i.e. before `Start()` on the same frame, or delay initialization until consent is collected):

```csharp
public class MyConsentDialog : MonoBehaviour
{
    public void OnPlayerAccepted()
    {
        LevelPlayHelper.Instance.SetUserConsent(true);
    }
}
```

> This package handles the technical wiring only. Whether GDPR/CCPA/COPPA applies to your app, and what consent flow satisfies it, is a legal question for your own counsel — see [Unity's regulation settings docs](https://docs.unity.com/en-us/grow/levelplay/sdk/unity/regulation-advanced-settings) for the authoritative reference.

## Impression-Level Revenue (ILRD) → Analytics

Forward paid impressions to your analytics/attribution platform via `OnAdRevenuePaid` (simple) or `OnImpressionDataReady` (full payload, background thread):

```csharp
using Firebase.Analytics;

void OnEnable() => LevelPlayHelper.OnImpressionDataReady += ForwardToFirebase;
void OnDisable() => LevelPlayHelper.OnImpressionDataReady -= ForwardToFirebase;

void ForwardToFirebase(LevelPlayImpressionData data)
{
    // Called on a background thread - if your analytics SDK requires the
    // main thread, marshal it yourself (e.g. via a thread-safe queue
    // drained in Update()).
    var parameters = new[]
    {
        new Parameter("ad_platform", "LevelPlay"),
        new Parameter("ad_source", data.AdNetwork),
        new Parameter("ad_format", data.AdFormat),
        new Parameter("value", data.Revenue ?? 0d),
        new Parameter("currency", "USD"),
    };
    FirebaseAnalytics.LogEvent("ad_impression", parameters);
}
```

## Extending: Game-Specific Subclass

`LevelPlayHelper` is designed to be subclassed so game-specific logic (ad cadence, dialogs, save-data hooks) stays in your project while SDK plumbing stays in the package:

```csharp
using Wagenheimer.LevelPlayHelper;

public class MyGameAdsHelper : LevelPlayHelper
{
    [SerializeField] private int normalAdInterval = 3;
    private int adsUntilNextInterstitial;

    public void CheckAdShowConditions()
    {
        if (--adsUntilNextInterstitial > 0)
            return;

        adsUntilNextInterstitial = normalAdInterval;
        ShowInterstitial();
    }
}
```

Add the subclass component instead of the base `LevelPlayHelper` to your persistent GameObject — the Setup and Config window and the custom inspector both work with any subclass automatically (they resolve `[CustomEditor(typeof(LevelPlayHelper), true)]` and search by base type).

## Banner Positioning

`BannerPositionPreset` mirrors the 9 standard LevelPlay banner positions and is mapped internally to `LevelPlayBannerPosition`:

`TopLeft` · `TopCenter` · `TopRight` · `CenterLeft` · `Center` · `CenterRight` · `BottomLeft` · `BottomCenter` (default) · `BottomRight`

## Runtime Debug Overlay

With `Enable Debug Overlay` on (default), `LevelPlayHelper` attaches a **runtime UI Toolkit panel** in the Unity Editor and Development Builds. Press **F8** (or tap the **ADS DBG** button) to open it. It is never created in release builds.

What it gives you:

- **"WHY NOT LOADING" banner** — `LevelPlayHelper.Diagnose()` picks the single most likely cause (init never completed, no App Key / Ad Unit ID, GDPR consent not granted, a format failing to load, Editor mock credentials) so you don't have to guess from the console.
- **Per-format state machine** — `NotConfigured → Idle → Loading(12s) → Ready → Showing → Closed / Failed`, with the last error code + message, retry attempt, **next-retry countdown**, and the last serving network / placement / revenue.
- **Honest credentials** — mock Editor credentials are labelled `(MOCK)` instead of being reported as missing.
- **Event log** — the helper's central log (500 entries, thread-safe), with a text filter, per-level colors and a **Copy Log** button.
- **Copy Report** — `BuildDiagnosticReport()` as text on the clipboard: Unity/device info, init state, credentials, per-format state, last errors and the log tail. Ideal for a bug report.
- **Actions** — force init, force reload, show each format, banner hide/destroy, consent on/off, try-any-ad and Test Suite.
- Safe-area aware, clamped to the screen, maximizable, and scalable at runtime with **A-** / **A+**.

Everything it shows is also available from code: `Diagnose()`, `BuildDiagnosticReport()`, `SnapshotLog()`, `InitState`, and the `InterstitialDiagnostics` / `RewardedDiagnostics` / `BannerDiagnostics` snapshots.

### Ads not loading?

1. Open the overlay (F8) and read the **WHY NOT LOADING** banner.
2. If it says the SDK init callback never arrived, that is expected in the Editor (mock ads do not need it) — the helper creates the ad objects anyway. On device, check the App Key and network.
3. Press **Copy Report** and attach it to your issue.

## Testing

Enable **Enable Test Suite** in the Inspector to launch the [LevelPlay Test Suite](https://docs.unity.com/en-us/grow/levelplay/sdk/unity/test-suite) automatically after init on device builds. **Disable it before releasing** — the Checklist tab will warn you if it's left on.

In the Unity Editor, mock ads are provided automatically — no configuration needed.

| Callback | Fires with mock ads? |
|---|---|
| `OnAdLoaded` | ✓ Always |
| `OnAdDisplayed` | ✓ |
| `OnAdRewarded` | ✓ (test reward) |
| `OnAdClosed` | ✓ |
| `OnAdLoadFailed` | ✕ Mock ads always succeed loading |
| `OnAdDisplayFailed` | ✕ |
| `OnAdClicked` | ✕ |
| `OnAdExpanded` / `OnAdCollapsed` | ✕ |
| `OnAdImpressionDataReady` | ✕ No impression data in Editor |

Validate error handling and revenue callbacks on a real device build with the Test Suite — mock ads only cover the happy path.

## Production Release Checklist

- [ ] `Setup & Config > Checklist` shows all automated checks passing (including the Dashboard cross-check)
- [ ] `Enable Test Suite` is **off** on every `LevelPlayHelper` instance
- [ ] Real App Key + Ad Unit IDs (not placeholders) for every platform you ship
- [ ] Tested with the LevelPlay Test Suite on a physical Android and/or iOS device
- [ ] GDPR/CCPA/COPPA settings reviewed with your own legal guidance if applicable
- [ ] iOS: ATT implemented and SKAdNetwork IDs configured in `Info.plist`
- [ ] Android: `AD_ID` permission present if targeting API 33+

## Auto-Update Notifications

The package ships an editor update checker. Once every 24h it compares the installed version against this repository's `package.json` and offers a one-click update when a new release is published. Check manually via **Tools > Wagenheimer > Level Play Helper > Check for Updates...**

Releases are cut automatically: every push to `master` bumps `package.json`'s version (patch/minor/major inferred from [Conventional Commits](https://www.conventionalcommits.org/) prefixes — `fix:`, `feat:`, `feat!:`/`BREAKING CHANGE`), updates `CHANGELOG.md`, tags the commit, and publishes a GitHub Release.

## Architecture

```
Your Game
  └── MyGameAdsHelper : LevelPlayHelper   ← game-specific cadence/UI (your project)
        └── LevelPlayHelper                ← this package: init, consent, lifecycle, retries
              └── Unity.Services.LevelPlay  ← Unity's LevelPlay SDK (com.unity.services.levelplay)
                    └── Mediated ad networks (Unity Ads, AdMob, Meta, etc.)
```

- **Runtime/LevelPlayHelper.cs** — the `MonoBehaviour` described throughout this README.
- **Runtime/LevelPlayDiagnostics.cs** — `SdkInitState` / `AdFormatState` / `AdLogLevel`, the `AdLogEntry` log entry and the per-format `AdFormatDiagnostics` snapshot.
- **Runtime/UI/LevelPlayDebugOverlay.cs** — the runtime UI Toolkit debug panel (code-only; no prefab).
- **Runtime/UI/Resources/LevelPlayDebugTheme.tss** — the default runtime theme the panel loads (`unity-theme://default`).
- **Editor/LevelPlayHelperEditor.cs** — custom Inspector (`[CustomEditor(typeof(LevelPlayHelper), true)]`, so it applies to subclasses too).
- **Editor/LevelPlaySetupWindow.cs** — the single Setup & Config `EditorWindow` (Credentials / Cloud / Checklist tabs).
- **Editor/SetupChecklistView.cs** — the checklist UI Toolkit view hosted by the window (eight sections, fixes, prompts, report).
- **Editor/LevelPlayCredentialsPanel.cs** — the Credentials tab (edits the helper prefab with live validation).
- **Editor/LevelPlayCloudPanel.cs** + **LevelPlayApiClient.cs** + **LevelPlayApiCredentials.cs** + **LevelPlayCloudCache.cs** — the Cloud (API) tab and its client.
- **Editor/CredentialValidation.cs** + **LevelPlayHelperLocator.cs** — shared credential validation and helper-prefab lookup.
- **Editor/LevelPlayHelperEditor.cs** — custom Inspector (`[CustomEditor(typeof(LevelPlayHelper), true)]`, so it applies to subclasses too).
- **Editor/UpdateChecker.cs** — opens Package Hub for this package (Check for updates button in the window).

## Troubleshooting / FAQ

**`CS0246: The type or namespace name 'Wagenheimer' could not be found`**
The package assembly failed to import or compile. Open the Console for the underlying error. Common causes: the Ads Mediation package didn't resolve, or (for git packages) a missing `.meta` file in an immutable `PackageCache` folder — Unity silently ignores assets without one instead of generating it. If you hit this on a freshly cloned/resolved copy, delete the `Library/PackageCache/com.wagenheimer.levelplayhelper@*` folder and the matching line in `Packages/packages-lock.json`, then let Package Manager re-resolve.

**Ads never load**
Run **Setup & Config > Checklist** first — the most common causes are an empty App Key/Ad Unit ID, an Ad Unit ID that does not belong to the App Key, a paused ad unit, an ad unit with no active network, or missing native dependency resolution (Android/iOS builds only; mock ads in the Editor don't need it). Then open the [Runtime Debug Overlay](#runtime-debug-overlay) (F8) and read the **WHY NOT LOADING** banner; **Copy Report** gives you a full diagnostic dump for a bug report. Note that the LevelPlay init callback does not fire in every Editor configuration — the helper detects that and creates the mock ad objects anyway after 6s.

**`ShowRewardedAd` granted the reward but no ad played**
Expected — that's the [fallback behavior](#fallback-behavior-rewarded-ads) when no rewarded inventory is available. Check `IsRewardedAdReady()` beforehand if your UX requires distinguishing "ad shown" from "reward granted for free."

**Do I need to call `LoadAd()` myself?**
No — the base class loads interstitial/rewarded ads automatically after init and after each show/close, with exponential backoff on failure. Banners load when you call `CreateBanner()`/`ShowBanner()`.

**Can I use this without subclassing?**
Yes — add `LevelPlayHelper` directly if you don't need custom cadence logic.

**Namespace/API errors after updating the Ads Mediation package**
Check `package.json`'s `dependencies` for the pinned `com.unity.services.levelplay` version and update it to match what Network Manager reports as installed.

## Versioning & Releases

This project follows [Semantic Versioning](https://semver.org/). See [CHANGELOG.md](CHANGELOG.md) for release history — entries are generated automatically from commit messages on every push to `master`.

## Contributing

Issues and PRs are welcome at [github.com/wagenheimer/UnityLevelPlayHelper](https://github.com/wagenheimer/UnityLevelPlayHelper). Please run the [Setup and Config](#setup-and-config-editor-tool) against a real project before submitting integration-related fixes, and keep commit messages in [Conventional Commits](https://www.conventionalcommits.org/) format — the release workflow depends on the prefix (`fix:`, `feat:`, `feat!:`) to pick the version bump.

## License

[MIT](LICENSE)
