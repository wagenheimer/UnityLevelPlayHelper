# Level Play Helper

Reusable Unity helper for **Unity LevelPlay (Ads Mediation)** — SDK initialization, GDPR/CCPA/COPPA consent flags, and interstitial / rewarded / banner lifecycle management with exponential-backoff load retries and impression-level revenue events.

## Installation (Unity Package Manager - Git URL)

1. Open **Window > Package Manager**
2. Click **+** > **Add package from git URL...**
3. Paste:

```
https://github.com/wagenheimer/UnityLevelPlayHelper.git
```

The [Unity Ads Mediation package](https://docs.unity.com/en-us/grow/levelplay/) (`com.unity.services.levelplay`) is installed automatically as a dependency.

### Requirements

- Unity 2021.3+
- Ads Mediation package 9.5.0+ (installed automatically)
- Native dependency resolution: run **Assets > External Dependency Manager > Android Resolver > Resolve** for Android builds, and install CocoaPods via **iOS Resolver > Install Cocoapods** for iOS.

## Setup

1. Add the `LevelPlayHelper` component to a persistent GameObject in your first scene.
2. Fill in the **App Key** from the [LevelPlay dashboard](https://platform.ironsrc.com/) for Android and/or iOS.
3. Fill in the Ad Unit IDs per platform. Leave an ID **empty** to disable that format on that platform.
4. (Optional) Configure GDPR / CCPA / COPPA settings under **Consent Settings**.

GDPR consent is read from `PlayerPrefs` key `UserConsent` (`1` = consented). Call `SetUserConsent(bool)` before initialization if you collect consent with your own CMP UI.

## Usage

```csharp
// Show an interstitial
LevelPlayHelper.Instance.ShowInterstitial();

// Show a rewarded ad with a reward callback
LevelPlayHelper.Instance.ShowRewardedAd(() =>
{
    // grant reward here - invoked only when the reward is earned
});

// Try any available ad, prioritizing rewarded
bool shown = LevelPlayHelper.Instance.TryShowAd();

// Banner
LevelPlayHelper.Instance.CreateBanner();
LevelPlayHelper.Instance.ShowBanner();
LevelPlayHelper.Instance.HideBanner();

// Readiness checks
bool ready = LevelPlayHelper.Instance.IsRewardedAdReady();
```

### Events

```csharp
LevelPlayHelper.OnSdkInitialized      += config => { };
LevelPlayHelper.OnInterstitialClosed  += () => { };
LevelPlayHelper.OnRewardedAdGranted   += () => { };
LevelPlayHelper.OnAdRevenuePaid       += (adUnitId, revenue) => { /* analytics */ };

// Raw impression-level revenue data (fires on a background thread!)
LevelPlayHelper.OnImpressionDataReady += data => { };
```

### Fallback behavior

`ShowRewardedAd` never blocks the player:

1. Rewarded ad is ready → show it and grant the reward through the callback.
2. Otherwise, interstitial is ready → show it and grant the reward immediately.
3. Nothing available → grant the reward directly.

## Testing

Enable **Enable Test Suite** in the Inspector to launch the [LevelPlay Test Suite](https://docs.unity.com/en-us/grow/levelplay/sdk/unity/test-suite) automatically after init on device builds. **Disable it before releasing.**

In the Unity Editor, mock ads are provided automatically — no configuration needed.

## Auto-update notifications

The package ships an editor update checker. Once every 24h it compares the installed version against this repository's `package.json` and offers a one-click update when a new release is published. Check manually via **Tools > Wagenheimer > Level Play Helper > Check for Updates...**

## License

[MIT](LICENSE)
