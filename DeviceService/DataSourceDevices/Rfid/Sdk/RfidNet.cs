using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Net;
using System.Net.Sockets;
namespace RfidSdk
{
    class RfidNet:Socket
    {
        const int recv_buff_size = 1024;
        const int send_buff_size = 512;
        byte[] sendBuff = null;
        byte[] recvBuff = null;
        TimeSpan oldRecvTime;
        EnRecvStage recvStage;
        int messageLen;
        int left = 0;
        RfidReader reader;
        public RfidNet(RfidReader reader, AddressFamily addressFamily,SocketType type,ProtocolType protoType) :base(addressFamily, type, protoType)
        {
            recvBuff = new byte[recv_buff_size];
            sendBuff = new byte[send_buff_size];
            oldRecvTime = new TimeSpan(DateTime.Now.Ticks);
            recvStage = EnRecvStage.EN_STAGE_IDLE;
            this.reader = reader;
        }

        public RfidNet(AddressFamily addressFamily, SocketType type, ProtocolType protoType) : base(addressFamily, type, protoType)
        {
            recvBuff = new byte[recv_buff_size];
            sendBuff = new byte[send_buff_size];
            oldRecvTime = new TimeSpan(DateTime.Now.Ticks);
            recvStage = EnRecvStage.EN_STAGE_IDLE;
        }

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

        public void HandleRecv()
        {
            int readLen = 0;
            while (Poll(0,SelectMode.SelectRead))
            {
                /*
                TimeSpan nowtime = new TimeSpan(DateTime.Now.Ticks);
                if (oldRecvTime.Subtract(nowtime).Duration().Milliseconds > 1000)
                {
                    recvStage = EnRecvStage.EN_STAGE_IDLE;
                }
                */
                if (ProtocolType == ProtocolType.Udp)
                {
                    messageLen = Receive(recvBuff, 0, recv_buff_size, SocketFlags.None);

                    if (recvBuff[messageLen - 1] == CaculateCheckSum(recvBuff, messageLen - 1))
                    {
                        reader.OnRecvCompleted(recvBuff, messageLen);
                    }
                    return;
                }
                try
                {
                    switch (recvStage)
                    {
                        case EnRecvStage.EN_STAGE_IDLE:
                            messageLen = 0;
                            Receive(recvBuff, messageLen, 1, SocketFlags.None);
                            if (recvBuff[messageLen] == 'R')
                            {
                                messageLen++;
                                recvStage = EnRecvStage.EN_STAGE_RECEIVE_HEAD;
                            }
                            else
                            {
                                break;
                            }
                            break;
                        case EnRecvStage.EN_STAGE_RECEIVE_HEAD:
                            Receive(recvBuff, messageLen, 1, SocketFlags.None);
                            if (recvBuff[messageLen] == 'F')
                            {
                                messageLen++;
                                recvStage = EnRecvStage.EN_STAGE_DATA;
                                left = 4;
                            }
                            else
                            {
                                recvStage = EnRecvStage.EN_STAGE_IDLE;
                            }
                            break;
                        case EnRecvStage.EN_STAGE_DATA:
                            readLen = Receive(recvBuff, messageLen, left, SocketFlags.None);
                            messageLen += readLen;
                            if (readLen >= left)
                            {
                                recvStage = EnRecvStage.EN_STAGE_PARAM_LEN;
                                left = 2;
                            }
                            else
                            {
                                left = left - readLen;
                            }
                            break;
                        case EnRecvStage.EN_STAGE_PARAM_LEN:
                            readLen = Receive(recvBuff, messageLen, left, SocketFlags.None);
                            messageLen += readLen;
                            if (readLen >= left)
                            {
                                recvStage = EnRecvStage.EN_STAGE_PARAM_DATA;
                                left = recvBuff[messageLen - 2];
                                left = left << 8;
                                left += recvBuff[messageLen - 1];
                            }
                            else
                            {
                                left = left - readLen;
                            }
                            if (left > 0xF000)
                            {
                                left = 0;
                            }
                            break;
                        case EnRecvStage.EN_STAGE_PARAM_DATA:
                            readLen = Receive(recvBuff, messageLen, left, SocketFlags.None);
                            messageLen += readLen;
                            if (readLen >= left)
                            {
                                recvStage = EnRecvStage.EN_STAGE_PARAM_CHECKSUM;
                            }
                            else
                            {
                                left = left - readLen;
                            }
                            break;
                        case EnRecvStage.EN_STAGE_PARAM_CHECKSUM:
                            Receive(recvBuff, messageLen, 1, SocketFlags.None);
                            messageLen++;
                            if (recvBuff[messageLen - 1] == CaculateCheckSum(recvBuff, messageLen - 1))
                            {
                                recvStage = EnRecvStage.EN_STAGE_COMPLETED;
                            }
                            else
                            {
                                recvStage = EnRecvStage.EN_STAGE_IDLE;
                            }
                            break;
                    }
                    if (EnRecvStage.EN_STAGE_COMPLETED == recvStage)
                    {
                        reader.OnRecvCompleted(recvBuff, messageLen);
                        recvStage = EnRecvStage.EN_STAGE_IDLE;
                        break;
                    }
                }
                catch(Exception)
                {
					recvStage = EnRecvStage.EN_STAGE_IDLE;
                }
            }
            readLen = 0;
        }
    }
}
