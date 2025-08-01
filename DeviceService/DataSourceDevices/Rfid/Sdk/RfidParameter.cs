namespace DeviceService.DataSourceDevices.Rfid.Sdk
{
    public class RfidWorkParam
    {
        public byte ucParamVersion;
        public byte ucRFPower;
        public byte ucScanInterval;
        public byte ucAutoTrigoffTime;
        public byte ucWorkMode;   //EnRfidWorkMode
        public byte ucInventoryArea;
        public byte ucInventoryAddress;
        public byte ucInventoryLength;
        public byte ucFilterTime;    //union:s
        public byte ucBeepOnFlag;
        public byte ucIsEnableRecord;
        public UInt16 usAntennaFlag;
        public UInt16 usDeviceAddr;
        //3 byte for reserved
        public void SetParamFromMessage(byte[] message, int offset)
        {
            ucParamVersion = message[offset++];
            ucRFPower = message[offset++];
            ucScanInterval = message[offset++];
            ucWorkMode = message[offset++];
            ucInventoryArea = message[offset++];
            ucInventoryAddress = message[offset++];
            ucInventoryLength = message[offset++];
            ucFilterTime = message[offset++];
            usDeviceAddr = message[offset++];
            usDeviceAddr = (UInt16)(usDeviceAddr << 8);
            usDeviceAddr += message[offset++];
            ucBeepOnFlag = message[offset++];
            ucIsEnableRecord =  message[offset++];
            ucAutoTrigoffTime = message[offset++];
            usAntennaFlag = message[offset++];
            usAntennaFlag = (UInt16)(usAntennaFlag << 8);
            usAntennaFlag += message[offset++];
        }

        public byte[] GetMessageDataFromParam()
        {
            byte[] message = new byte[15];
            int offset = 0;
            message[offset++] = 5;
            message[offset++] = ucRFPower;
            message[offset++] = ucScanInterval;
            message[offset++] = ucWorkMode;
            message[offset++] = ucInventoryArea;
            message[offset++] = ucInventoryAddress;
            message[offset++] = ucInventoryLength;
            message[offset++] = ucFilterTime;
            message[offset++] = (byte)(usDeviceAddr >> 8);
            message[offset++] = (byte)(usDeviceAddr & 0xFF);
            message[offset++] = ucBeepOnFlag;
            message[offset++] = ucIsEnableRecord;
            message[offset++] = ucAutoTrigoffTime;
            message[offset++] = (byte)(usAntennaFlag >> 8);
            message[offset++] = (byte)(usAntennaFlag & 0xFF);
            return message;
        }
    }


    public class RfidTransmissionParam
    {
        public byte ucParamVersion;
        public byte ucBaudRate;   //EnRfidBaudRate 232 or 485
        public byte ucTransferLink;  //which way to send the tag data to host.
        public byte ucTransferProtocol;
        public byte ucWiegandProtocol;    //EnRfidWiegandProto
        public byte ucWiegandPulseWidth;      //10 us
        public byte ucWiegandPulsePeriod;     //100 us
        public byte ucWiegandInterval;
        public byte ucWiegandPosition;
        public byte ucWiegandDirection;
        public byte[] mac_addr;   //6 bytes
        public byte[] local_ip;    //4bytes
        public UInt16 local_port;
        public byte[] sub_mask_addr; //4bytes
        public byte[] gateway;//4bytes
        public byte[] remote_ip_addr;//4bytes
        public UInt16 remote_port;
        public byte config_ip_mode;	//0:get ip from flash config 1:get it from dhcp
        public byte heartBeates;
        public char[] syris_module_sn;  //8 bytes
        public char syris_module_id;
        public RfidTransmissionParam()
        {
            mac_addr = new byte[6];
            local_ip = new byte[4];
            sub_mask_addr = new byte[4];
            gateway = new byte[4];
            remote_ip_addr = new byte[4];
            syris_module_sn = new char[9];
        }
        public void SetParamFromMessage(byte[] array, int offset)
        {
            int pos = offset;
            ucParamVersion = array[pos++];
            ucBaudRate = array[pos++];
            ucTransferLink = array[pos++];
            ucTransferProtocol = array[pos++];
            ucWiegandProtocol = array[pos++];
            ucWiegandPulseWidth = array[pos++];
            ucWiegandPulsePeriod = array[pos++];
            ucWiegandInterval = array[pos++];
            ucWiegandPosition = array[pos++];
            ucWiegandDirection = array[pos++];
            Array.Copy(array, pos, mac_addr, 0, 6);
            pos += 6;
            Array.Copy(array, pos, local_ip, 0, 4);
            pos += 4;
            local_port = array[pos++];
            local_port = (UInt16)(local_port << 8);
            local_port += array[pos++];
            Array.Copy(array, pos, sub_mask_addr, 0, 4);
            pos += 4;
            Array.Copy(array, pos, gateway, 0, 4);
            pos += 4;
            Array.Copy(array, pos, remote_ip_addr, 0, 4);
            pos += 4;
            remote_port = array[pos++];
            remote_port = (UInt16)(remote_port << 8);
            remote_port += array[pos++];
            config_ip_mode = array[pos++];
            heartBeates = array[pos++];
            for (int index = 0; index < 8; index++)
            {
                syris_module_sn[index] = (char)array[pos++];
            }
            syris_module_sn[8] = '\0';
            syris_module_id = (char)array[pos++];
        }

        public byte[] GetMessageDataFromParam()
        {
            int pos = 0;
            byte[] array = new byte[64];
            byte[] result = null;
            array[pos++] = 5;
            array[pos++] = ucBaudRate;
            array[pos++] = ucTransferLink;
            array[pos++] = ucTransferProtocol;
            array[pos++] = ucWiegandProtocol;
            array[pos++] = ucWiegandPulseWidth;
            array[pos++] = ucWiegandPulsePeriod;
            array[pos++] = ucWiegandInterval;
            array[pos++] = ucWiegandPosition;
            array[pos++] = ucWiegandDirection;
            Array.Copy(mac_addr, 0, array, pos, 6);
            pos += 6;
            Array.Copy(local_ip, 0, array, pos, 4);
            pos += 4;
            array[pos++] = (byte)((local_port >> 8) & 0xFF);
            array[pos++] = (byte)(local_port & 0xFF);
            Array.Copy(sub_mask_addr, 0, array, pos, 4);
            pos += 4;
            Array.Copy(gateway, 0, array, pos, 4);
            pos += 4;
            Array.Copy(remote_ip_addr, 0, array, pos, 4);
            pos += 4;
            array[pos++] = (byte)((remote_port >> 8) & 0xFF);
            array[pos++] = (byte)(remote_port & 0xFF);
            array[pos++] = config_ip_mode;
            array[pos++] = heartBeates;
            for (int index = 0; index < 8;index++)
            {
                array[pos++] = (byte)syris_module_sn[index];
            }
            array[pos++] = (byte)syris_module_id;
            result = new byte[pos];
            Array.Copy(array, 0, result, 0, pos);
            return result;
        }
    }


    public class RfidAdvanceParam
    {
        public byte ucInitFlag;
        public byte ucRegion;
        public byte ucChannelIndex;
        public byte ucFreqHoppingFlag;
        public byte ucCWFlag; //cw
        public byte sel_flag;    //00 01:all 10~sl 11sl
        public byte session; //s0 00 s1:01 s2:10 s3:11
        public byte target;  //A 0  b:1
        public byte QValue; //4bit
        public byte rssiValue;
        //3 byte for reserved

        public void SetParamFromMessage(byte[] message, int offset)
        {
            ucInitFlag = message[offset++];
            ucRegion = message[offset++];
            ucChannelIndex = message[offset++];
            ucFreqHoppingFlag = message[offset++];
            ucCWFlag += message[offset++];
            sel_flag = message[offset++];
            session = message[offset++];
            target = message[offset++];
            QValue = message[offset++];
            rssiValue = message[offset++];
        }

        public byte[] GetMessageDataFromParam()
        {
            byte[] message = new byte[10];
            int offset = 0;
            message[offset++] = ucInitFlag;
            message[offset++] = ucRegion;
            message[offset++] = ucChannelIndex;
            message[offset++] = ucFreqHoppingFlag;
            message[offset++] = ucCWFlag;
            message[offset++] = sel_flag;
            message[offset++] = session;
            message[offset++] = target;
            message[offset++] = QValue;
            message[offset++] = rssiValue;
            return message;
        }
    }

    public class RfidExtParam
    {
        public byte ucRelayMode;
        public byte ucRelayTime;
        public byte ucVerifyFlag;
        public UInt16 usVerifyPwd;
        public RfidExtParam()
        {
            ucRelayMode = 0;
            ucRelayTime = 0;
            ucVerifyFlag = 0;
            usVerifyPwd = 0;
        }
        public void SetParamFromMessage(byte[] array, int offset)
        {
            int pos = offset;
            ucRelayMode = array[pos++];
            ucRelayTime = array[pos++];
            ucVerifyFlag = array[pos++];
            usVerifyPwd = array[pos++];
            usVerifyPwd = (UInt16)(usVerifyPwd << 8);
            usVerifyPwd = (UInt16)(usVerifyPwd + array[pos++]);
        }

        public byte[] GetMessageDataFromParam()
        {
            int pos = 0;
            byte[] array = new byte[5];
            array[pos++] = ucRelayMode;
            array[pos++] = ucRelayTime;
            array[pos++] = ucVerifyFlag;
            array[pos++] = (byte)((usVerifyPwd >> 8) & 0xFF);
            array[pos++] = (byte)(usVerifyPwd & 0xFF);
            return array;
        }
    }

    public class TlvValueItem
    {
        public byte _tlvType { get; set; }
        public byte _tlvLen { get; set; }
        public byte[] _tlvValue { get; set; }
    }
}
