using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace RfidSdk
{
    public abstract class RfidReader
    {
        protected Transport transport;

        //receive data and handle it
        public abstract void OnRecvCompleted(byte[] messageData,int messageLen);

        public abstract void HandleRecvData();

        public void SetSerialParam(String serialPortName,int baudRate)
        {
            transport.SetSerialPortParam(serialPortName, baudRate);
        }

        public void SetEthernetParam(String localIP, UInt16 localPort, String remoteIP, UInt16 remotePort, TransportType type)
        {
            transport.SetIPParam(localIP, localPort, remoteIP, remotePort, type);
        }

        public Boolean RequestResource()
        {
            return transport.RequestResource();
        }

        public void ReleaseResource()
        {
            transport.ReleaseResource();
        }

        protected void FillCmdHeader(byte[] buff,byte frameCode)
        {
            int iIndex = 0;
            buff[iIndex++] = (byte)'R';
            buff[iIndex++] = (byte)'F';
            buff[iIndex++] = 0; //frame type
            buff[iIndex++] = 0;
            buff[iIndex++] = 0;
            buff[iIndex++] = frameCode;
        }

        protected byte CaculateCheckSum(byte[] recv_buff, int recv_len)
        {
            byte checksum = 0;
            for (int iIndex = 0; iIndex < recv_len; iIndex++)
            {
                checksum += recv_buff[iIndex];
            }
            checksum = (byte)(~checksum + 1);
            return checksum;
        }

        //Cmd for readers
        public void QueryDeviceInfo()
        {
            int pos = 6;
            byte temp = 0;
            byte[] cmdBuff = new byte[16];
            FillCmdHeader(cmdBuff, 0x40);
            cmdBuff[pos++] = 0;
            cmdBuff[pos++] = 0;
            temp = CaculateCheckSum(cmdBuff, pos);
            cmdBuff[pos++] = temp;
            transport.SendData(cmdBuff,0,pos);
            
        }

        public void StartInventory()
        {
            int pos = 6;
            byte temp = 0;
            byte[] cmdBuff = new byte[16];
            FillCmdHeader(cmdBuff, 0x21);
            cmdBuff[pos++] = 0;
            cmdBuff[pos++] = 0;
            temp = CaculateCheckSum(cmdBuff, pos);
            cmdBuff[pos++] = temp;
            transport.SendData(cmdBuff, 0, pos);
        }

        public void StopInventory()
        {
            int pos = 6;
            byte temp = 0;
            byte[] cmdBuff = new byte[16];
            FillCmdHeader(cmdBuff, 0x23);
            cmdBuff[pos++] = 0;
            cmdBuff[pos++] = 0;
            temp = CaculateCheckSum(cmdBuff, pos);
            cmdBuff[pos++] = temp;
            transport.SendData(cmdBuff, 0, pos);
        }

        public void QueryWorkingParam()
        {
            int pos = 6;
            byte temp = 0;
            byte[] cmdBuff = new byte[16];
            FillCmdHeader(cmdBuff, 0x42);
            cmdBuff[pos++] = 0;
            cmdBuff[pos++] = 0;
            temp = CaculateCheckSum(cmdBuff, pos);
            cmdBuff[pos++] = temp;
            transport.SendData(cmdBuff, 0, pos);
        }

        public void SetDefaultParam()
        {
            int pos = 6;
            byte temp = 0;
            byte[] cmdBuff = new byte[16];
            FillCmdHeader(cmdBuff, 0x12);
            cmdBuff[pos++] = 0;
            cmdBuff[pos++] = 0;
            temp = CaculateCheckSum(cmdBuff, pos);
            cmdBuff[pos++] = temp;
            transport.SendData(cmdBuff, 0, pos);
        }

        public void InventoryOnce()
        {
            int pos = 6;
            byte temp = 0;
            byte[] cmdBuff = new byte[16];
            FillCmdHeader(cmdBuff, 0x22);
            cmdBuff[pos++] = 0;
            cmdBuff[pos++] = 0;
            temp = CaculateCheckSum(cmdBuff, pos);
            cmdBuff[pos++] = temp;
            transport.SendData(cmdBuff, 0, pos);
        }

        public void GetEpcData()
        {
            int pos = 6;
            byte temp = 0;
            byte[] cmdBuff = new byte[16];
            FillCmdHeader(cmdBuff, 0x24);
            cmdBuff[pos++] = 0;
            cmdBuff[pos++] = 0;
            temp = CaculateCheckSum(cmdBuff, pos);
            cmdBuff[pos++] = temp;
            transport.SendData(cmdBuff, 0, pos);
        }

        public void ResetReader()
        {
            int pos = 6;
            byte temp = 0;
            byte[] cmdBuff = new byte[16];
            FillCmdHeader(cmdBuff, 0x10);
            cmdBuff[pos++] = 0;
            cmdBuff[pos++] = 0;
            temp = CaculateCheckSum(cmdBuff, pos);
            cmdBuff[pos++] = temp;
            transport.SendData(cmdBuff, 0, pos);
        }

        public void SetWorkingParam(RfidWorkParam workParam)
        {
            int pos = 6;
            byte temp = 0;
            byte[] cmdBuff = new byte[64];
            byte[] param_data = workParam.GetMessageDataFromParam();
            FillCmdHeader(cmdBuff, 0x41);
            cmdBuff[pos++] = (byte)(param_data.Length >> 8);
            cmdBuff[pos++] = (byte)(param_data.Length + 2);
            //add param ltv
            cmdBuff[pos++] = 0x23;
            cmdBuff[pos++] = (byte)param_data.Length;
            for (int index = 0; index < param_data.Length;index++)
            {
                cmdBuff[pos++] = param_data[index];
            }
            temp = CaculateCheckSum(cmdBuff, pos);
            cmdBuff[pos++] = temp;
            transport.SendData(cmdBuff, 0, pos);
        }

        public void QueryTransferParam()
        {
            int pos = 6;
            byte temp = 0;
            byte[] cmdBuff = new byte[16];
            FillCmdHeader(cmdBuff, 0x43);
            cmdBuff[pos++] = 0;
            cmdBuff[pos++] = 0;
            temp = CaculateCheckSum(cmdBuff, pos);
            cmdBuff[pos++] = temp;
            transport.SendData(cmdBuff, 0, pos);
        }

        public void SetTransferParam(RfidTransmissionParam transParam)
        {
            int pos = 6;
            byte temp = 0;
            byte[] cmdBuff = new byte[64];
            byte[] param_data = transParam.GetMessageDataFromParam();
            FillCmdHeader(cmdBuff, 0x44);
            cmdBuff[pos++] = (byte)(param_data.Length >> 8);
            cmdBuff[pos++] = (byte)(param_data.Length + 2);
            //add param ltv
            cmdBuff[pos++] = 0x24;
            cmdBuff[pos++] = (byte)param_data.Length;
            for (int index = 0; index < param_data.Length; index++)
            {
                cmdBuff[pos++] = param_data[index];
            }
            temp = CaculateCheckSum(cmdBuff, pos);
            cmdBuff[pos++] = temp;
            transport.SendData(cmdBuff, 0, pos);
        }

        public void QueryAdvanceParam()
        {
            int pos = 6;
            byte temp = 0;
            byte[] cmdBuff = new byte[16];
            FillCmdHeader(cmdBuff, 0x45);
            cmdBuff[pos++] = 0;
            cmdBuff[pos++] = 0;
            temp = CaculateCheckSum(cmdBuff, pos);
            cmdBuff[pos++] = temp;
            transport.SendData(cmdBuff, 0, pos);
        }

        public void QueryExtParam()
        {
            int pos = 6;
            byte temp = 0;
            byte[] cmdBuff = new byte[16];
            FillCmdHeader(cmdBuff, 0x3E);
            cmdBuff[pos++] = 0;
            cmdBuff[pos++] = 0;
            temp = CaculateCheckSum(cmdBuff, pos);
            cmdBuff[pos++] = temp;
            transport.SendData(cmdBuff, 0, pos);
        }

        public void SetExtParam(RfidExtParam extParam)
        {
            int pos = 6;
            byte temp = 0;
            byte[] cmdBuff = new byte[64];
            byte[] param_data = extParam.GetMessageDataFromParam();
            FillCmdHeader(cmdBuff, 0x3F);
            cmdBuff[pos++] = (byte)(param_data.Length >> 8);
            cmdBuff[pos++] = (byte)(param_data.Length + 2);
            //add param tlv
            cmdBuff[pos++] = 0x29;
            cmdBuff[pos++] = (byte)param_data.Length;
            for (int index = 0; index < param_data.Length; index++)
            {
                cmdBuff[pos++] = param_data[index];
            }
            temp = CaculateCheckSum(cmdBuff, pos);
            cmdBuff[pos++] = temp;
            transport.SendData(cmdBuff, 0, pos);
        }

        public void SetAdvanceParam(RfidAdvanceParam advanceParam)
        {
            int pos = 6;
            byte temp = 0;
            byte[] cmdBuff = new byte[64];
            byte[] param_data = advanceParam.GetMessageDataFromParam();
            FillCmdHeader(cmdBuff, 0x46);
            cmdBuff[pos++] = (byte)(param_data.Length >> 8);
            cmdBuff[pos++] = (byte)(param_data.Length + 2);
            //add param ltv
            cmdBuff[pos++] = 0x25;
            cmdBuff[pos++] = (byte)param_data.Length;
            for (int index = 0; index < param_data.Length; index++)
            {
                cmdBuff[pos++] = param_data[index];
            }
            temp = CaculateCheckSum(cmdBuff, pos);
            cmdBuff[pos++] = temp;
            transport.SendData(cmdBuff, 0, pos);
        }

        public void SetSingleParam(byte parameterType,byte[] parameterValue)
        {
            if (null == parameterValue)
            {
                return;
            }
            UInt16 length = (UInt16)(parameterValue.Length + 3);
            int pos = 6;
            byte temp = 0;
            byte[] cmdBuff = new byte[64];
            FillCmdHeader(cmdBuff, 0x48);
            cmdBuff[pos++] = 0x00;
            cmdBuff[pos++] = 0x00;
            //add param ltv
            cmdBuff[pos++] = 0x26;
            cmdBuff[pos++] = (byte)(1+ parameterValue.Length);
            cmdBuff[pos++] = parameterType;
            for (int index = 0; index < parameterValue.Length; index++)
            {
                cmdBuff[pos++] = parameterValue[index];
            }
            cmdBuff[7] = (byte)(pos - 8);
            temp = CaculateCheckSum(cmdBuff, pos);
            cmdBuff[pos++] = temp;
            transport.SendData(cmdBuff, 0, pos);
        }

        public void SetUsbInfo(byte interfaceType,byte usbProto,byte enterFlag)
        {
            int pos = 6;
            byte temp = 0;
            byte[] cmdBuff = new byte[64];
            byte[] param_data = new byte[2];
            FillCmdHeader(cmdBuff, 0x50);
            cmdBuff[pos++] = (byte)(param_data.Length >> 8);
            cmdBuff[pos++] = (byte)(param_data.Length);
            cmdBuff[pos++] = interfaceType;
            cmdBuff[pos++] = usbProto;
            cmdBuff[pos++] = enterFlag;
            cmdBuff[7] = (byte)(pos - 8);
            temp = CaculateCheckSum(cmdBuff, pos);
            cmdBuff[pos++] = temp;
            transport.SendData(cmdBuff, 0, pos);
        }

        public void QueryUsbInfoParam()
        {
            int pos = 6;
            byte temp = 0;
            byte[] cmdBuff = new byte[16];
            FillCmdHeader(cmdBuff, 0x51);
            cmdBuff[pos++] = 0;
            cmdBuff[pos++] = 0;
            temp = CaculateCheckSum(cmdBuff, pos);
            cmdBuff[pos++] = temp;
            transport.SendData(cmdBuff, 0, pos);
        }

        public void QUeryDatainfoFlag()
        {
            int pos = 6;
            byte temp = 0;
            byte[] cmdBuff = new byte[16];
            FillCmdHeader(cmdBuff, 0x53);
            cmdBuff[pos++] = 0;
            cmdBuff[pos++] = 0;
            temp = CaculateCheckSum(cmdBuff, pos);
            cmdBuff[pos++] = temp;
            transport.SendData(cmdBuff, 0, pos);
        }

        public void SetDataInfoFlag(UInt16 dataFlag, byte ddataFormat)
        {
            int pos = 6;
            byte temp = 0;
            byte[] cmdBuff = new byte[64];
            byte[] param_data = new byte[3];
            FillCmdHeader(cmdBuff, 0x52);
            cmdBuff[pos++] = (byte)(param_data.Length >> 8);
            cmdBuff[pos++] = (byte)(param_data.Length & 0xFF);

            cmdBuff[pos++] = (byte)(dataFlag >> 8);
            cmdBuff[pos++] = (byte)(dataFlag & 0xFF);
            cmdBuff[pos++] = ddataFormat;
            temp = CaculateCheckSum(cmdBuff, pos);
            cmdBuff[pos++] = temp;
            transport.SendData(cmdBuff, 0, pos);
        }
        public void QuerySingleParam(byte parameterType)
        {
            int pos = 6;
            byte temp = 0;
            byte[] cmdBuff = new byte[16];
            FillCmdHeader(cmdBuff, 0x49);
            cmdBuff[pos++] = 0;
            cmdBuff[pos++] = 3;
            cmdBuff[pos++] = 0x26;
            cmdBuff[pos++] = 1;
            cmdBuff[pos++] = parameterType;
            temp = CaculateCheckSum(cmdBuff, pos);
            cmdBuff[pos++] = temp;
            transport.SendData(cmdBuff, 0, pos);
        }


        public void WriteTag(byte membank,UInt16 startAddress,byte length,byte[] writenContent,byte[] password)
        {
            int pos = 6;
            byte temp = 0;
            byte[] cmdBuff = new byte[128];
            if ((length * 2) > writenContent.Length)
            {
                return;
            }
            FillCmdHeader(cmdBuff, 0x30);
            cmdBuff[pos++] = 0;
            cmdBuff[pos++] = 0;

            //add param ltv
            cmdBuff[pos++] = 0x08;
            cmdBuff[pos++] = (byte)(8+length*2);
            if (password == null)
            {
                for (int index = 0; index < 4; index++)
                {
                    cmdBuff[pos++] = 0;
                }
            }
            else
            {
                for (int index = 0; index < 4; index++)
                {
                    cmdBuff[pos++] = password[index];
                }
            }
            
            cmdBuff[pos++] = 0x01;
            cmdBuff[pos++] = membank;
            cmdBuff[pos++] = (byte)(startAddress >> 8);
            cmdBuff[pos++] = (byte)(startAddress & 0xFF);
            cmdBuff[pos++] = length;
            for (int index = 0; index < (length * 2); index++)
            {
                cmdBuff[pos++] = writenContent[index];
            }
            cmdBuff[7] = (byte)(pos - 8);
            temp = CaculateCheckSum(cmdBuff, pos);
            cmdBuff[pos++] = temp;
            transport.SendData(cmdBuff, 0, pos);
        }

        public void WriteEpc(byte membank, UInt16 startAddress, byte length, byte[] writenContent, byte[] password)
        {
            int pos = 6;
            byte temp = 0;
            byte[] cmdBuff = new byte[128];
            if ((length * 2) > writenContent.Length)
            {
                return;
            }
            FillCmdHeader(cmdBuff, 0x35);
            cmdBuff[pos++] = 0;
            cmdBuff[pos++] = 0;

            //add param ltv
            cmdBuff[pos++] = 0x08;
            cmdBuff[pos++] = (byte)(8 + length * 2);
            if (password == null)
            {
                for (int index = 0; index < 4; index++)
                {
                    cmdBuff[pos++] = 0;
                }
            }
            else
            {
                for (int index = 0; index < 4; index++)
                {
                    cmdBuff[pos++] = password[index];
                }
            }

            cmdBuff[pos++] = 0x01;
            cmdBuff[pos++] = membank;
            cmdBuff[pos++] = (byte)(startAddress >> 8);
            cmdBuff[pos++] = (byte)(startAddress & 0xFF);
            cmdBuff[pos++] = length;
            for (int index = 0; index < (length * 2); index++)
            {
                cmdBuff[pos++] = writenContent[index];
            }
            cmdBuff[7] = (byte)(pos - 8);
            temp = CaculateCheckSum(cmdBuff, pos);
            cmdBuff[pos++] = temp;
            transport.SendData(cmdBuff, 0, pos);
        }

        public void UploadRecord()
        {
            int pos = 6;
            byte temp = 0;
            byte[] cmdBuff = new byte[16];
            FillCmdHeader(cmdBuff, 0x72);
            cmdBuff[pos++] = 0;
            cmdBuff[pos++] = 0;
            temp = CaculateCheckSum(cmdBuff, pos);
            cmdBuff[pos++] = temp;
            transport.SendData(cmdBuff, 0, pos);
        }

        public void QueryTime()
        {
            int pos = 6;
            byte temp = 0;
            byte[] cmdBuff = new byte[16];
            FillCmdHeader(cmdBuff, 0x4A);
            cmdBuff[pos++] = 0;
            cmdBuff[pos++] = 0;
            temp = CaculateCheckSum(cmdBuff, pos);
            cmdBuff[pos++] = temp;
            transport.SendData(cmdBuff, 0, pos);
        }

        public void SetTime(int year,int month,int day,int hour,int min,int sec)
        {
            int pos = 6;
            byte temp = 0;
            byte[] cmdBuff = new byte[32];
            FillCmdHeader(cmdBuff, 0x4B);
            cmdBuff[pos++] = 0;
            cmdBuff[pos++] = 0;
            //add time ltv
            cmdBuff[pos++] = 0x06;
            cmdBuff[pos++] = 7;
            cmdBuff[pos++] = (byte)(year >> 8);
            cmdBuff[pos++] = (byte)(year & 0xFF); ;
            cmdBuff[pos++] = (byte)month;
            cmdBuff[pos++] = (byte)day;
            cmdBuff[pos++] = (byte)hour;
            cmdBuff[pos++] = (byte)min;
            cmdBuff[pos++] = (byte)sec;
            cmdBuff[7] = (byte)(pos - 8);
            temp = CaculateCheckSum(cmdBuff, pos);
            cmdBuff[pos++] = temp;
            transport.SendData(cmdBuff, 0, pos);
        }
        public void WiegandWriteTag(UInt32 wiegand_number, byte[] password)
        {
            int pos = 6;
            byte temp = 0;
            byte[] cmdBuff = new byte[128];
            byte[] writenContent = new byte[4];
            byte length = 2;
            FillCmdHeader(cmdBuff, 0x32);
            cmdBuff[pos++] = 0;
            cmdBuff[pos++] = 0;

            //add param ltv
            cmdBuff[pos++] = 0x08;
            //only 2 word will be written
            cmdBuff[pos++] = (byte)(8 + length * 2);
            if (password == null)
            {
                for (int index = 0; index < 4; index++)
                {
                    cmdBuff[pos++] = 0;
                }
            }
            else
            {
                for (int index = 0; index < 4; index++)
                {
                    cmdBuff[pos++] = password[index];
                }
            }

            writenContent[0] = (byte)(wiegand_number >> 24);
            writenContent[1] = (byte)(wiegand_number >> 16);
            writenContent[2] = (byte)(wiegand_number >> 8);
            writenContent[3] = (byte)(wiegand_number & 0xFF);
            cmdBuff[pos++] = 0x01;
            cmdBuff[pos++] = 0; //membank can be set to zero
            cmdBuff[pos++] = 0;
            cmdBuff[pos++] = length;
            for (int index = 0; index < (length * 2); index++)
            {
                cmdBuff[pos++] = writenContent[index];
            }
            cmdBuff[7] = (byte)(pos - 8);
            temp = CaculateCheckSum(cmdBuff, pos);
            cmdBuff[pos++] = temp;
            transport.SendData(cmdBuff, 0, pos);
        }

        public void ReadTag(byte membank, UInt16 startAddress, byte length, byte[] password)
        {
            int pos = 6;
            byte temp = 0;
            byte[] cmdBuff = new byte[128];
            FillCmdHeader(cmdBuff, 0x31);
            cmdBuff[pos++] = 0;
            cmdBuff[pos++] = 0;

            //add param ltv
            cmdBuff[pos++] = 0x08;
            cmdBuff[pos++] = 8;
            for (int index = 0; index < 4; index++)
            {
                cmdBuff[pos++] = password[index];
            }
            cmdBuff[pos++] = 0x00;
            cmdBuff[pos++] = membank;
            cmdBuff[pos++] = (byte)(startAddress >> 8);
            cmdBuff[pos++] = (byte)(startAddress & 0xFF);
            cmdBuff[pos++] = length;
            
            cmdBuff[7] = (byte)(pos - 8);
            temp = CaculateCheckSum(cmdBuff, pos);
            cmdBuff[pos++] = temp;
            transport.SendData(cmdBuff, 0, pos);
        }

        public void LockTag(byte membank,  byte[] password)
        {
            int pos = 6;
            byte temp = 0;
            byte[] cmdBuff = new byte[128];
            FillCmdHeader(cmdBuff, 0x33);
            cmdBuff[pos++] = 0;
            cmdBuff[pos++] = 0;

            //add param ltv
            cmdBuff[pos++] = 0x08;
            cmdBuff[pos++] = 8;
            for (int index = 0; index < 4; index++)
            {
                cmdBuff[pos++] = password[index];
            }
            cmdBuff[pos++] = 0x02;
            cmdBuff[pos++] = membank;
            cmdBuff[pos++] = 0;
            cmdBuff[pos++] = 0;

            cmdBuff[7] = (byte)(pos - 8);
            temp = CaculateCheckSum(cmdBuff, pos);
            cmdBuff[pos++] = temp;
            transport.SendData(cmdBuff, 0, pos);
        }

        public void KillTag(byte[] password,byte[] killpassword)
        {
            int pos = 6;
            byte temp = 0;
            byte[] cmdBuff = new byte[128];
            FillCmdHeader(cmdBuff, 0x34);
            cmdBuff[pos++] = 0;
            cmdBuff[pos++] = 0;

            //add param ltv
            cmdBuff[pos++] = 0x08;
            cmdBuff[pos++] = 8;
            for (int index = 0; index < 4; index++)
            {
                cmdBuff[pos++] = password[index];
            }
            cmdBuff[pos++] = 0x03;
            cmdBuff[pos++] = 0;
            cmdBuff[pos++] = 0;
            cmdBuff[pos++] = 0;
            /*
            cmdBuff[pos++] = killpassword[0];
            cmdBuff[pos++] = killpassword[1];
            cmdBuff[pos++] = killpassword[2];
            cmdBuff[pos++] = killpassword[3];
            */
            cmdBuff[7] = (byte)(pos - 8);
            temp = CaculateCheckSum(cmdBuff, pos);
            cmdBuff[pos++] = temp;
            transport.SendData(cmdBuff, 0, pos);
        }

        public void AudioPlay(String audio_text)
        {
            int pos = 6;
            int index = 0;
            byte temp = 0;
            byte[] audio_bytes = Encoding.GetEncoding("GBK").GetBytes(audio_text);
            byte[] cmdBuff = new byte[128];
            FillCmdHeader(cmdBuff, 0x4D);
            cmdBuff[pos++] = 0;
            cmdBuff[pos++] = 0;

            //add param ltv
            cmdBuff[pos++] = 0x28;
            cmdBuff[pos++] = (byte)(audio_bytes.Length+1);
            cmdBuff[pos++] = 0x01;  //播放语音
            for (index = 0;index < audio_bytes.Length; index++)
            {
                cmdBuff[pos++] = (byte)audio_bytes[index];
            }
            cmdBuff[7] = (byte)(pos - 8);
            temp = CaculateCheckSum(cmdBuff, pos);
            cmdBuff[pos++] = temp;
            transport.SendData(cmdBuff, 0, pos);
        }

        public void AudioSetOfflineContext(String audio_text)
        {
            int pos = 6;
            int index = 0;
            byte temp = 0;
            byte[] audio_bytes = Encoding.GetEncoding("GBK").GetBytes(audio_text);
            byte[] cmdBuff = new byte[128];
            FillCmdHeader(cmdBuff, 0x4D);
            cmdBuff[pos++] = 0;
            cmdBuff[pos++] = 0;

            //add param ltv
            cmdBuff[pos++] = 0x28;
            cmdBuff[pos++] = (byte)(audio_bytes.Length + 1);
            cmdBuff[pos++] = 0x02;  //设置离线语音内容
            for (index = 0; index < audio_bytes.Length; index++)
            {
                cmdBuff[pos++] = (byte)audio_bytes[index];
            }
            cmdBuff[7] = (byte)(pos - 8);
            temp = CaculateCheckSum(cmdBuff, pos);
            cmdBuff[pos++] = temp;
            transport.SendData(cmdBuff, 0, pos);
        }

        public void RelayOperation(byte relay_no,byte op_type,byte op_time)
        {
            int pos = 6;
            int index = 0;
            byte temp = 0;
            byte[] cmdBuff = new byte[128];
            FillCmdHeader(cmdBuff, 0x4C);
            cmdBuff[pos++] = 0;
            cmdBuff[pos++] = 0;

            //add param ltv
            cmdBuff[pos++] = (byte)Tlv_Attr_Code.TLV_ATTR_RELAY;
            index = pos;
            cmdBuff[pos++] = 0x00;
            if ( (relay_no & 0x01) != 0)
            {
                cmdBuff[pos++] = 1;
                cmdBuff[pos++] = op_type;
                cmdBuff[pos++] = op_time;
            }
            if ((relay_no & 0x02) != 0)
            {
                cmdBuff[pos++] = 2;
                cmdBuff[pos++] = op_type;
                cmdBuff[pos++] = op_time;
            }
            cmdBuff[index] = (byte)(pos - index-1);
            cmdBuff[7] = (byte)(pos - 8);
            temp = CaculateCheckSum(cmdBuff, pos);
            cmdBuff[pos++] = temp;
            transport.SendData(cmdBuff, 0, pos);
        }

        public void AddVerifyToTag()
        {
            int pos = 6;
            byte temp = 0;
            byte[] cmdBuff = new byte[64];
            FillCmdHeader(cmdBuff, 0x4E);
            cmdBuff[pos++] = 0;
            cmdBuff[pos++] = 0;
            cmdBuff[7] = (byte)(pos - 8);
            temp = CaculateCheckSum(cmdBuff, pos);
            cmdBuff[pos++] = temp;
            transport.SendData(cmdBuff, 0, pos);
        }

        public void QueryModebusParam()
        {
            int pos = 6;
            byte temp = 0;
            byte[] cmdBuff = new byte[64];
            FillCmdHeader(cmdBuff, 0x55);
            cmdBuff[pos++] = 0;
            cmdBuff[pos++] = 0;
            cmdBuff[7] = (byte)(pos - 8);
            temp = CaculateCheckSum(cmdBuff, pos);
            cmdBuff[pos++] = temp;
            transport.SendData(cmdBuff, 0, pos);
        }

        public void SetModbusParam(byte tagNum,byte unionsize,byte startAddr,byte clearFlag,byte modbusproto)
        {
            int pos = 6;
            byte temp = 0;
            byte[] cmdBuff = new byte[64];
            FillCmdHeader(cmdBuff, 0x54);
            cmdBuff[pos++] = 0;
            cmdBuff[pos++] = 0;
            //add param
            cmdBuff[pos++] = tagNum;
            cmdBuff[pos++] = unionsize;
            cmdBuff[pos++] = startAddr;
            cmdBuff[pos++] = clearFlag;
            cmdBuff[pos++] = modbusproto;
            cmdBuff[7] = (byte)(pos - 8);
            temp = CaculateCheckSum(cmdBuff, pos);
            cmdBuff[pos++] = temp;
            transport.SendData(cmdBuff, 0, pos);
        }
    }
}
