using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace HeadlessMinecraftControl
{
    public enum MinecraftState
    {
        Stopped,
        Starting,
        Running,
        Stopping,
        Launched,
        Failed,
        LoggedIn,
        MainMenu,
        Connected,
        Connecting,
        Reconnecting,
        InGame,
        Disconnected
    }
}
