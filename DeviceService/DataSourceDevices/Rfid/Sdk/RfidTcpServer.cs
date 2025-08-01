using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Net.Sockets;
using System.Net;

namespace RfidSdk
{
    class RfidTcpServer: IDisposable
    {
        #region Fields
        /// <summary>
        /// the max connection allow
        /// </summary>
        private int _maxClient;

        /// <summary>
        /// 当前的连接的客户端数
        /// </summary>
        private int _clientCount;

        /// <summary>
        /// 服务器使用的异步socket
        /// </summary>
        private Socket _serverSock;

        /// <summary>
        /// 客户端会话列表
        /// </summary>
        private List<AsyncSocketState> _clients;

        private bool disposed = false;

        #endregion

        #region Properties

        /// <summary>
        /// 服务器是否正在运行
        /// </summary>
        public bool IsRunning { get; private set; }
        /// <summary>
        /// 监听的IP地址
        /// </summary>
        public IPAddress Address { get; private set; }
        /// <summary>
        /// 监听的端口
        /// </summary>
        public int Port { get; private set; }
        /// <summary>
        /// 通信使用的编码
        /// </summary>
        public Encoding Encoding { get; set; }

        #endregion
        public RfidTcpServer()
        {
        }
        #region 构造函数
        /// <summary>
        /// 异步Socket TCP服务器
        /// </summary>
        /// <param name="localIPAddress">监听的IP地址</param>
        /// <param name="listenPort">监听的端口</param>
        /// <param name="maxClient">最大客户端数量</param>
        public RfidTcpServer(IPAddress localIPAddress, int listenPort, int maxClient)
        {
            this.Address = localIPAddress;
            this.Port = listenPort;
            this.Encoding = Encoding.Default;

            _maxClient = maxClient;
            _clients = new List<AsyncSocketState>();
            _serverSock = new Socket(localIPAddress.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        }

        #endregion

        #region Method

        /// <summary>
        /// 启动服务器
        /// </summary>
        public bool SetServerAddress(String localIP, int listenPort, int maxClient)
        {
            IPAddress localAddress;
            if (true == IPAddress.TryParse(localIP, out localAddress))
            {
                this.Address = localAddress;
            }
            else
            {
                return false;
            }
            this.Port = listenPort;
            this.Encoding = Encoding.Default;

            _maxClient = maxClient;
            _clients = new List<AsyncSocketState>();
            _serverSock = new Socket(localAddress.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            return true;
        }

        /// <summary>
        /// 启动服务器
        /// </summary>
        public void Start()
        {
            if (!IsRunning)
            {
                IsRunning = true;
                _serverSock.Bind(new IPEndPoint(this.Address, this.Port));
                _serverSock.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, 1);
                _serverSock.Listen(128);
                _serverSock.BeginAccept(new AsyncCallback(HandleAcceptConnected), _serverSock);
            }
        }

        /// <summary>
        /// 停止服务器
        /// </summary>
        public void Stop()
        {
            if (IsRunning)
            {
                IsRunning = false;
                //TODO close all the connect
                lock(_clients)
                {
                    
                    foreach (AsyncSocketState reader in _clients.ToArray())
                    {
                        //reader.ClientSocket.EndReceive(new AsyncCallback(HandleEndAccept));
                        reader.ClientSocket.Shutdown(SocketShutdown.Both);
                        //reader.ClientSocket.Close();
                        reader.ClientSocket.Disconnect(false);
                        _clients.Remove(reader);
                    }
                    _serverSock.Close();
                }
            }
        }

        private void HandleEndAccept(IAsyncResult ar)
        {

        }
        /// <summary>
        /// 处理客户端连接
        /// </summary>
        /// <param name="ar"></param>
        private void HandleAcceptConnected(IAsyncResult ar)
        {
            if (IsRunning)
            {
                Socket server = (Socket)ar.AsyncState;
                Socket client = server.EndAccept(ar);

                //检查是否达到最大的允许的客户端数目
                if (_clientCount >= _maxClient)
                {
                    //C-TODO 触发事件
                    RaiseOtherException(null);
                }
                else
                {
                    AsyncSocketState state = new AsyncSocketState(client);
                    lock (_clients)
                    {
                        _clients.Add(state);
                        _clientCount++;
                        RaiseClientConnected(state); //触发客户端连接事件
                    }
                    //state.RecvDataBuffer = new byte[client.ReceiveBufferSize];
                    //开始接受来自该客户端的数据
                    client.BeginReceive(state.RecvDataBuffer, 0, state.RecvDataBuffer.Length, SocketFlags.None,
                     new AsyncCallback(HandleDataReceived), state);
                }
                //接受下一个请求
                server.BeginAccept(new AsyncCallback(HandleAcceptConnected), ar.AsyncState);
            }
        }

        /// <summary>
        /// 处理客户端数据
        /// </summary>
        /// <param name="ar"></param>
        private void HandleDataReceived(IAsyncResult ar)
        {
            int recv = 0;
            if (IsRunning)
            {
                AsyncSocketState state = (AsyncSocketState)ar.AsyncState;
                Socket client = state.ClientSocket;
                try
                {
                    //如果两次开始了异步的接收,所以当客户端退出的时候
                    //会两次执行EndReceive
                    recv = client.EndReceive(ar);
                    
                }
                catch (SocketException)
                {
                    recv = 0;
                }

                if (recv == 0)
                {
                    //if remote disconnect or something wrong happen
                    lock (_clients)
                    {
                        RaiseClientDisconnected(state);
                        _clients.Remove(state);
                        Close(state);
                    }
                }
                else
                {
                    //C- TODO 触发数据接收事件
                    RaiseDataReceived(state);
                    //继续接收来自来客户端的数据
                    try
                    {
                        client.BeginReceive(state.RecvDataBuffer, 0, state.RecvDataBuffer.Length, SocketFlags.None,
                        new AsyncCallback(HandleDataReceived), state);
                    }
                    catch(Exception)
                    {

                    }
                }
            }
        }

        /// <summary>
        /// 发送数据
        /// </summary>
        /// <param name="state">接收数据的客户端会话</param>
        /// <param name="data">数据报文</param>
        public void Send(AsyncSocketState state, byte[] data)
        {
            RaisePrepareSend(state);
            Send(state.ClientSocket, data);
        }

        /// <summary>
        /// 异步发送数据至指定的客户端
        /// </summary>
        /// <param name="client">客户端</param>
        /// <param name="data">报文</param>
        public void Send(Socket client, byte[] data)
        {
            if (!IsRunning)
                throw new InvalidProgramException("This TCP Scoket server has not been started.");

            if (client == null)
                throw new ArgumentNullException("client");

            if (data == null)
                throw new ArgumentNullException("data");
            client.BeginSend(data, 0, data.Length, SocketFlags.None,
             new AsyncCallback(SendDataEnd), client);
        }

        /// <summary>
        /// 发送数据完成处理函数
        /// </summary>
        /// <param name="ar">目标客户端Socket</param>
        private void SendDataEnd(IAsyncResult ar)
        {
            ((Socket)ar.AsyncState).EndSend(ar);
            RaiseCompletedSend(null);
        }
        #endregion

        #region 事件

        /// <summary>
        /// 与客户端的连接已建立事件
        /// </summary>
        public event EventHandler<AsyncSocketEventArgs> ClientConnected;
        /// <summary>
        /// 与客户端的连接已断开事件
        /// </summary>
        public event EventHandler<AsyncSocketEventArgs> ClientDisconnected;

        /// <summary>
        /// 触发客户端连接事件
        /// </summary>
        /// <param name="state"></param>
        private void RaiseClientConnected(AsyncSocketState state)
        {
            if (ClientConnected != null)
            {
                ClientConnected(this, new AsyncSocketEventArgs(state));
            }
        }
        /// <summary>
        /// 触发客户端连接断开事件
        /// </summary>
        /// <param name="client"></param>
        private void RaiseClientDisconnected(AsyncSocketState client)
        {
            if (ClientDisconnected != null)
            {
                ClientDisconnected(this, new AsyncSocketEventArgs(client));
            }
        }
#if false
        public void HandleRecv(AsyncSocketState clientState)
        {
            int readLen = 0;
            int left = 0;
            while (clientState.ClientSocket.Poll(0, SelectMode.SelectRead))
            {
                /*
                TimeSpan nowtime = new TimeSpan(DateTime.Now.Ticks);
                if (oldRecvTime.Subtract(nowtime).Duration().Milliseconds > 1000)
                {
                    recvStage = EnRecvStage.EN_STAGE_IDLE;
                }
                */
                switch (clientState._recvStage)
                {
                    case EnRecvStage.EN_STAGE_IDLE:
                        clientState._messageLen = 0;
                        clientState.ClientSocket.Receive(clientState._recvBuffer, clientState._messageLen, 1, SocketFlags.None);
                        if (clientState.RecvDataBuffer[clientState._messageLen] == 'R')
                        {
                            clientState._messageLen++;
                            clientState._recvStage = EnRecvStage.EN_STAGE_RECEIVE_HEAD;
                        }
                        else
                        {
                            break;
                        }
                        break;
                    case EnRecvStage.EN_STAGE_RECEIVE_HEAD:
                        clientState.ClientSocket.Receive(clientState._recvBuffer, clientState._messageLen, 1, SocketFlags.None);
                        if (clientState._recvBuffer[clientState._messageLen] == 'F')
                        {
                            clientState._messageLen++;
                            clientState._recvStage = EnRecvStage.EN_STAGE_DATA;
                            left = 4;
                        }
                        else
                        {
                            clientState._recvStage = EnRecvStage.EN_STAGE_IDLE;
                        }
                        break;
                    case EnRecvStage.EN_STAGE_DATA:
                        readLen = clientState.ClientSocket.Receive(clientState._recvBuffer, clientState._messageLen, left, SocketFlags.None);
                        clientState._messageLen += readLen;
                        if (clientState._messageLen >= left)
                        {
                            clientState._recvStage = EnRecvStage.EN_STAGE_PARAM_LEN;
                            left = 2;
                        }
                        else
                        {
                            left = left - clientState._messageLen;
                        }
                        break;
                    case EnRecvStage.EN_STAGE_PARAM_LEN:
                        readLen = clientState.ClientSocket.Receive(clientState._recvBuffer, clientState._messageLen, left, SocketFlags.None);
                        clientState._messageLen += readLen;
                        if (clientState._messageLen >= left)
                        {
                            clientState._recvStage = EnRecvStage.EN_STAGE_PARAM_DATA;
                            left = clientState._recvBuffer[clientState._messageLen - 2];
                            left = left << 8;
                            left += clientState._recvBuffer[clientState._messageLen - 1];
                        }
                        else
                        {
                            left = left - readLen;
                        }
                        if (left == 0xF000)
                        {
                            left = 0;
                        }
                        break;
                    case EnRecvStage.EN_STAGE_PARAM_DATA:
                        readLen = clientState.ClientSocket.Receive(clientState._recvBuffer, clientState._messageLen, left, SocketFlags.None);
                        clientState._messageLen += readLen;
                        if (clientState._messageLen >= left)
                        {
                            clientState._recvStage = EnRecvStage.EN_STAGE_PARAM_CHECKSUM;
                        }
                        else
                        {
                            left = left - readLen;
                        }
                        break;
                    case EnRecvStage.EN_STAGE_PARAM_CHECKSUM:
                        clientState.ClientSocket.Receive(clientState._recvBuffer, clientState._messageLen, 1, SocketFlags.None);
                        clientState._messageLen++;
                        if (clientState._recvBuffer[clientState._messageLen - 1] == CaculateCheckSum(clientState._recvBuffer, clientState._messageLen - 1))
                        {
                            RaiseDataReceived(clientState);
                        }
                        clientState._recvStage = EnRecvStage.EN_STAGE_IDLE;
                        break;
                }
            }

        }
#endif
        public byte CaculateCheckSum(byte[] recv_buff, int recv_len)
        {
            byte checksum = 0;
            for (int iIndex = 0; iIndex < recv_len; iIndex++)
            {
                checksum += recv_buff[iIndex];
            }
            checksum = (byte)(~checksum + 1);
            return checksum;
        }
        /// <summary>
        /// 接收到数据事件
        /// </summary>
        public event EventHandler<AsyncSocketEventArgs> DataReceived;

        private void RaiseDataReceived(AsyncSocketState state)
        {

            if (DataReceived != null)
            {
                DataReceived(this, new AsyncSocketEventArgs(state));
            }
        }

        /// <summary>
        /// 发送数据前的事件
        /// </summary>
        public event EventHandler<AsyncSocketEventArgs> PrepareSend;

        /// <summary>
        /// 触发发送数据前的事件
        /// </summary>
        /// <param name="state"></param>
        private void RaisePrepareSend(AsyncSocketState state)
        {
            if (PrepareSend != null)
            {
                PrepareSend(this, new AsyncSocketEventArgs(state));
            }
        }

        /// <summary>
        /// 数据发送完毕事件
        /// </summary>
        public event EventHandler<AsyncSocketEventArgs> CompletedSend;

        /// <summary>
        /// 触发数据发送完毕的事件
        /// </summary>
        /// <param name="state"></param>
        private void RaiseCompletedSend(AsyncSocketState state)
        {
            if (CompletedSend != null)
            {
                CompletedSend(this, new AsyncSocketEventArgs(state));
            }
        }

        /// <summary>
        /// 网络错误事件
        /// </summary>
        public event EventHandler<AsyncSocketEventArgs> NetError;
        /// <summary>
        /// 触发网络错误事件
        /// </summary>
        /// <param name="state"></param>
        private void RaiseNetError(AsyncSocketState state)
        {
            if (NetError != null)
            {
                NetError(this, new AsyncSocketEventArgs(state));
            }
        }

        /// <summary>
        /// 异常事件
        /// </summary>
        public event EventHandler<AsyncSocketEventArgs> OtherException;
        /// <summary>
        /// 触发异常事件
        /// </summary>
        /// <param name="state"></param>
        private void RaiseOtherException(AsyncSocketState state, string descrip)
        {
            if (OtherException != null)
            {
                OtherException(this, new AsyncSocketEventArgs(descrip, state));
            }
        }
        private void RaiseOtherException(AsyncSocketState state)
        {
            RaiseOtherException(state, "");
        }
        #endregion

        #region Close
        /// <summary>
        /// 关闭一个与客户端之间的会话
        /// </summary>
        /// <param name="state">需要关闭的客户端会话对象</param>
        public void Close(AsyncSocketState state)
        {
            if (state != null)
            {
                state.Datagram = null;
                state.RecvDataBuffer = null;

                _clients.Remove(state);
                _clientCount--;
                //TODO 触发关闭事件
                state.Close();
            }
        }
        /// <summary>
        /// 关闭所有的客户端会话,与所有的客户端连接会断开
        /// </summary>
        public void CloseAllClient()
        {
            foreach (AsyncSocketState client in _clients)
            {
                Close(client);
            }
            _clientCount = 0;
            _clients.Clear();
        }
        #endregion

        #region 释放
        /// <summary>
        /// Performs application-defined tasks associated with freeing, 
        /// releasing, or resetting unmanaged resources.
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Releases unmanaged and - optionally - managed resources
        /// </summary>
        /// <param name="disposing"><c>true</c> to release 
        /// both managed and unmanaged resources; <c>false</c> 
        /// to release only unmanaged resources.</param>
        protected virtual void Dispose(bool disposing)
        {
            if (!this.disposed)
            {
                if (disposing)
                {
                    try
                    {
                        Stop();
                        if (_serverSock != null)
                        {
                            _serverSock = null;
                        }
                    }
                    catch (SocketException)
                    {
                        //TODO
                        RaiseOtherException(null);
                    }
                }
                disposed = true;
            }
        }
        #endregion
    }
}
