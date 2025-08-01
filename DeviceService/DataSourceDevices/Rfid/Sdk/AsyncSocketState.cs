using System.Net.Sockets;

namespace DeviceService.DataSourceDevices.Rfid.Sdk
{
    public class AsyncSocketEventArgs : EventArgs
    {
        /// <summary>
        /// 提示信息
        /// </summary>
        public string Msg;

        /// <summary>
        /// 客户端状态封装类
        /// </summary>
        public AsyncSocketState State;

        /// <summary>
        /// 是否已经处理过了
        /// </summary>
        public bool IsHandled { get; set; }

        public AsyncSocketEventArgs(string msg)
        {
            this.Msg = msg;
            IsHandled = false;
        }
        public AsyncSocketEventArgs(AsyncSocketState state)
        {
            this.State = state;
            IsHandled = false;
        }
        public AsyncSocketEventArgs(string msg, AsyncSocketState state)
        {
            this.Msg = msg;
            this.State = state;
            IsHandled = false;
        }
    }
    public class AsyncSocketState
    {
            #region 字段
            /// <summary>
            /// 接收数据缓冲区
            /// </summary>
            public byte[] RecvBuffer;

            /// <summary>
            /// 客户端发送到服务器的报文
            /// 注意:在有些情况下报文可能只是报文的片断而不完整
            /// </summary>
            private string _datagram;

            /// <summary>
            /// 客户端的Socket
            /// </summary>
            private Socket _clientSock;

            public EnRecvStage RecvStage { get; set; }
            public int MessageLen { get; set; }
            #endregion

        #region 属性

        /// <summary>
        /// 接收数据缓冲区 
        /// </summary>
        public byte[] RecvDataBuffer
            {
                get
                {
                    return RecvBuffer;
                }
                set
                {
                    RecvBuffer = value;
                }
            }

            /// <summary>
            /// 存取会话的报文
            /// </summary>
            public string Datagram
            {
                get
                {
                    return _datagram;
                }
                set
                {
                    _datagram = value;
                }
            }

            /// <summary>
            /// 获得与客户端会话关联的Socket对象
            /// </summary>
            public Socket ClientSocket
            {
                get
                {
                    return _clientSock;

                }
            }


            #endregion

            /// <summary>
            /// 构造函数
            /// </summary>
            /// <param name="cliSock">会话使用的Socket连接</param>
            public AsyncSocketState(Socket cliSock)
            {
                _clientSock = cliSock;
                InitBuffer();
            }

            /// <summary>
            /// 初始化数据缓冲区
            /// </summary>
            public void InitBuffer()
            {
                if (RecvBuffer == null && _clientSock != null)
                {
                    RecvBuffer = new byte[_clientSock.ReceiveBufferSize];
                }
            }

            /// <summary>
            /// 关闭会话
            /// </summary>
            public void Close()
            {

                //关闭数据的接受和发送
                _clientSock.Shutdown(SocketShutdown.Both);

                //清理资源
                _clientSock.Close();
            }
        }
    }
