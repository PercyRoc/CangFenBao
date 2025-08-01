using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.IO.Ports;
namespace RfidSdk
{
    public enum EnRecvStage
    {
        EN_STAGE_IDLE = 0,
        EN_STAGE_RECEIVE_HEAD,
        EN_STAGE_DATA,
        EN_STAGE_PARAM_LEN,
        EN_STAGE_PARAM_DATA,
        EN_STAGE_PARAM_CHECKSUM,
        EN_STAGE_COMPLETED
    }

    class RfidSerialPort:SerialPort
    {
        const int recv_buff_size = 1024;
        const int send_buff_size = 512;
        byte[] sendBuff = null;
        byte[] recvBuff = null;
        TimeSpan oldRecvTime;
        EnRecvStage recvStage;
        int messageLen;
        byte temp = 0;
        int left = 0;
        RfidReader reader;
        public RfidSerialPort(String serialPortName,int baudRate,RfidReader reader):base(serialPortName,baudRate)
        {
            this.PortName = serialPortName;
            this.BaudRate = baudRate;
            this.StopBits = StopBits.One;
            this.Parity = Parity.None;
            this.DataBits = 8;
            recvBuff = new byte[recv_buff_size];
            sendBuff = new byte[send_buff_size];
            oldRecvTime = new TimeSpan(DateTime.Now.Ticks);
            recvStage = EnRecvStage.EN_STAGE_IDLE;
            this.reader = reader;
            Close();
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
            while (BytesToRead > 0)
            {
                /*
                TimeSpan nowtime = new TimeSpan(DateTime.Now.Ticks);
                if (oldRecvTime.Subtract(nowtime).Duration().Milliseconds > 1000)
                {
                    recvStage = EnRecvStage.EN_STAGE_IDLE;
                }
                */
                switch (recvStage)
                {
                    case EnRecvStage.EN_STAGE_IDLE:
                        messageLen = 0;
                        Array.Clear(recvBuff, 0, 20);
                        temp = (byte)ReadByte();
                        if (temp == 'R')
                        {
                            recvBuff[messageLen++] = temp;
                            recvStage = EnRecvStage.EN_STAGE_RECEIVE_HEAD;
                        }
                        else
                        {
                            break;
                        }
                        break;
                    case EnRecvStage.EN_STAGE_RECEIVE_HEAD:
                        temp = (byte)ReadByte();
                        if (temp == 'F')
                        {
                            recvBuff[messageLen++] = temp;
                            recvStage = EnRecvStage.EN_STAGE_DATA;
                            left = 4;
                        }
                        else
                        {
                            recvStage = EnRecvStage.EN_STAGE_IDLE;
                        }
                        break;
                    case EnRecvStage.EN_STAGE_DATA:
                        readLen = Read(recvBuff, messageLen, left);
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
                        readLen = Read(recvBuff, messageLen, left);
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
                        if (left > 0x100)
                        {
                            left = 0;
                        }
                        break;
                    case EnRecvStage.EN_STAGE_PARAM_DATA:
                        readLen = Read(recvBuff, messageLen, left);
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
                        temp = (byte)ReadByte();
                        recvBuff[messageLen++] = temp;
                        if (temp == CaculateCheckSum(recvBuff, messageLen - 1))
                        {
                            recvStage = EnRecvStage.EN_STAGE_COMPLETED;
                            reader.OnRecvCompleted(recvBuff, messageLen);
                        }
                        recvStage = EnRecvStage.EN_STAGE_IDLE;
                        break;
                }
            }
            
        }
    }
}
