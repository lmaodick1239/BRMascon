# BRMascon

BRMascon is a C# (.NET 9.0) application that translates the DirectInput signals from a **Zuiki one-handle Mascon** into virtual Xbox 360 controller inputs. It is specifically designed to be used with **Roblox British Rail**.

## Known Issues

- **Input Lag:** Due to the action queuing system, there may be a slight delay (up to 100ms) between physical input and in-game response, especially during rapid transitions.
- **Missmatches Controller position:** In some cases, the virtual controller may not perfectly reflect the physical mascon position, particularly when rapidly changing between power and brake notches. You can reset the controller position by applying full power and back to neutral on notched power trains, or by applying full service brakes on station stop with guard.
- **Limited Train Profiles:** Currently supports only a few British Rail train classes. Additional profiles may be needed for other classes or custom configurations.

## Features

- **Zuiki Mascon Support:** Maps the DirectInput of the Zuiki one-handle mascon (VID 33DD, PID 0001) to expected virtual inputs.
- **Multiple Train Configurations:** Built-in profiles for various British Rail train classes, handling differences in power notches and brake configurations (notched vs. unnotched).

## Supported Train Classes

- **Class 800-810:** 4 power notches, unnotched brakes
- **Class 230:** 6 power notches, unnotched brakes
- **Class 90:** 7 power notches, unnotched brakes
- **Class 142/153:** 7 power notches, 3 notched brakes
- **Class 231/745/755/756:** unnotched power, unnotched brakes

## Requirements

1. **Physical Zuiki Mascon** connected to your PC.
2. **ViGEmBus Driver:** Must be installed to emulate the virtual Xbox 360 controller. You can download and install it from the [ViGEmBus Releases page](https://github.com/ViGEm/ViGEmBus/releases). The app will fail immediately without this driver installed.
3. **.NET 9.0 SDK:** Required to build and run the project.

## Controls Mapping

| Physical Mascon | Virtual Xbox 360 Controller | In-Game Action |
| --- | --- | --- |
| Handle Forward | `ZR` (Right Trigger) taps | Less Brake / More Power |
| Handle Back | `R` (Right Bumper) / `ZL` (Left Trigger) taps | More Brake / Less Power |
| Emergency Brake Position | `R3` (Right Stick Click) Hold | Emergency Brake Engage |
| `A` / `X` | `B` / `X` | AWS / Interaction |
| D-Pad | D-Pad (POV hat switch) | Camera/menus views |

## How to Build and Run

1. Clone or download this repository.
2. Open a terminal and navigate to the `BRMascon` project directory:
   ```powershell
   cd BRMascon
   ```
3. Build the project:
   ```powershell
   dotnet build
   ```
4. Run the project:
   ```powershell
   dotnet run
   ```

## Configuration

You can access the runtime configuration menu by pressing the **ESC** key while the application is running in the console. This allows you to select the appropriate train class profile for your current Roblox session.
