using System;
namespace Nightcall;
public static class NightcallClock
{
    public static bool IsNight(DateTime local) => local.Hour < 7 || local.Hour >= 19;
}
