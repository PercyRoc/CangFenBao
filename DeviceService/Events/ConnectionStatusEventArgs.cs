using System;

namespace DeviceService.Events // You might need to adjust this namespace based on your project structure
{
    public class ConnectionStatusEventArgs : EventArgs
    {
        public bool IsConnected { get; }

        public ConnectionStatusEventArgs(bool isConnected)
        {
            IsConnected = isConnected;
        }
    }
} 