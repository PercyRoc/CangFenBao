namespace DeviceService.DataSourceDevices.Rfid.Sdk
{
    class RfidReaderManager
    {
        private static RfidReaderManager _instance = null;
        Boolean end_thread_flag = false;
        Thread recv_thread = null;
        private List<RfidReader> readers = new List<RfidReader>();
        Mutex listMutex = new Mutex(false);
        private RfidReaderManager()
        {
            recv_thread = new Thread(HandleRecvThread);
            recv_thread.IsBackground = true;
            recv_thread.Start();
            readers.Clear();
        }

        public static RfidReaderManager Instance()
        {
            if (_instance == null)
            {
                _instance = new RfidReaderManager();
            }
            return _instance;
        }

        private void HandleRecvThread()
        {
            while(false == end_thread_flag)
            {

                try
                {
                    listMutex.WaitOne();
                    foreach (RfidReader reader in readers.ToArray())
                    {
                        reader.HandleRecvData();
                    }
                }
                finally
                {
                    listMutex.ReleaseMutex();
                }
                Thread.Sleep(10);
            }
        }

        private void AddRfidReader(RfidReader reader)
        {
            try
            {
                listMutex.WaitOne();
                readers.Add(reader);
            }
            finally
            {
                listMutex.ReleaseMutex();
            }
        }

        private void DelRfidReader(RfidReader reader)
        {
            try
            {
                    listMutex.WaitOne();
                    readers.Remove(reader);
            }
            finally
            {
                listMutex.ReleaseMutex();
            }
        }

        public void DeleteAllReader()
        {
            try
            {
                listMutex.WaitOne();
                foreach (RfidReader reader in readers.ToArray())
                {
                    reader.ReleaseResource();
                }
                readers.Clear();
            }
            finally
            {
                listMutex.ReleaseMutex();
            }
        }

        public RfidReader CreateReaderInSerialPort(String portName,int baudRate,RfidReaderRspNotify notify)
        {
            RfidReader reader = new MSerialReader(notify);
            bool result = false;
            
            reader.SetSerialParam(portName, baudRate);
            result = reader.RequestResource();
            if (true == result)
            {
                Instance().AddRfidReader(reader);
                return reader;
            }
            else
            {
                return null;
            }
        }

        public RfidReader CreateReaderInNet(String localIP,UInt16 localPort,String readerIP,UInt16 readerPort,TransportType type, RfidReaderRspNotify notify)
        {
            RfidReader reader = new MSerialReader(notify);
            reader.SetEthernetParam(localIP, localPort, readerIP, readerPort, type);
            if (reader.RequestResource())
            {
                Instance().AddRfidReader(reader);
                return reader;
            }
            else
            {
                return null;
            }
        }

        public void ReleaseRfidReader(RfidReader reader)
        {
            if (null == reader)
            {
                return;
            }
            Instance().DelRfidReader(reader);
            reader.ReleaseResource();
        }
    }
}
