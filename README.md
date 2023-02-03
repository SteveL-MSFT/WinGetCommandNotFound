# WinGetCommandNotFound

This is a proof-of-concept implementing both the `IFeedbackProvider` and `ICommandPredictor` interfaces.
`IFeedbackProvider` requires PS7.4+.

**This is NOT intended to be used outside of a demo with no intent to take this to production**

![Screen-Recording-2022-12-27-at-8](https://user-images.githubusercontent.com/11859881/209662484-c739d16b-3dbd-44be-84b5-2402bcfadbbe.gif)

## Feedback provider

The feedback provider uses the existing sqlite database used by winget instead of downloading and expanding
its own copy.

Because the `WindowsApps` folder is protected, this implementation currently uses a hardcoded path to the
`index.db` file in "$env:ProgramFiles\WindowsApps\Microsoft.Winget.Source_2022.1227.1114.286_neutral__8wekyb3d8bbwe\public\index.db".
A different version of Winget will use a different path.

I've only tested this on win-arm64, but it builds for win-x64 runtime, so it should work.

## Command predictor

PSReadLine currently does not have a way to present a prediction without a keypress.
The suggestion from the feedback provider is given as a prediction, but will require pressing `w` for the prediction to show.

## Building

Go to `src` folder and use `dotnet build`.  Requires .NET 7 SDK installed and in path.

## Using

In the published folder, just `Import-Module WinGetCommandNotFound.psd1` which will register the Feedback Provider and Predictor
Then type a command you don't have installed.
