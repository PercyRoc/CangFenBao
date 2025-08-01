using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Net.Sockets;
namespace RfidSdk
{
    public class AsyncSocketEventArgs : EventArgs
    {
        /// <summary>
        /// 提示信息
        /// </summary>
        public string _msg;

        /// <summary>
        /// 客户端状态封装类
        /// </summary>
        public AsyncSocketState _state;

        /// <summary>
        /// 是否已经处理过了
        /// </summary>
        public bool IsHandled { get; set; }

        public AsyncSocketEventArgs(string msg)
        {
            this._msg = msg;
            IsHandled = false;
        }
        public AsyncSocketEventArgs(AsyncSocketState state)
        {
            this._state = state;
            IsHandled = false;
        }
        public AsyncSocketEventArgs(string msg, AsyncSocketState state)
        {
            this._msg = msg;
            this._state = state;
            IsHandled = false;
        }
    }
    public class AsyncSocketState
    {
            #region 字段
            /// <summary>
            /// 接收数据缓冲区
            /// </summary>
            public byte[] _recvBuffer;

            /// <summary>
            /// 客户端发送到服务器的报文
            /// 注意:在有些情况下报文可能只是报文的片断而不完整
            /// </summary>
            private string _datagram;

            /// <summary>
            /// 客户端的Socket
            /// </summary>
            private Socket _clientSock;

            public EnRecvStage _recvStage { get; set; }
            public int _messageLen { get; set; }
            #endregion

        #region 属性

        /// <summary>
        /// 接收数据缓冲区 
        /// </summary>
        public byte[] RecvDataBuffer
            {
                get
                {
                    return _recvBuffer;
                }
                set
                {
                    _recvBuffer = value;
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
                if (_recvBuffer == null && _clientSock != null)
                {
                    _recvBuffer = new byte[_clientSock.ReceiveBufferSize];
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
