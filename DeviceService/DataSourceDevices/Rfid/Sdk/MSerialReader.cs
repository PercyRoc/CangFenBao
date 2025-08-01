namespace DeviceService.DataSourceDevices.Rfid.Sdk
{
    public class MSerialReader:RfidReader
    {
        RfidReaderRspNotify _notifyImpl;
        private const int Message_Len_Start_Pos = 6;
        private const int Message_Frame_Code_Pos = 5;
        private const int Message_Attr_Start_Pos = 8;


        public MSerialReader(RfidReaderRspNotify notifyImpl)
        {
            this._notifyImpl = notifyImpl;
            transport = new Transport(this);
        }
        
        private int GetPositionByAttrType(byte[] message,int messageLen,byte attrType)
        {
            int attrPos = -1;
            int attrLen = 0;
            int curPos = 0;
            attrLen = message[Message_Len_Start_Pos];
            attrLen = attrLen << 8;
            attrLen += message[Message_Len_Start_Pos + 1];

            curPos = Message_Attr_Start_Pos;
            while(curPos < (Message_Attr_Start_Pos + attrLen))
            {
                if (message[curPos] == attrType)
                {
                    attrPos = curPos;
                    break;
                }
                else
                {
                    curPos = curPos + message[curPos+1] + 2;
                }
            }
            return attrPos;
        }

        public void OnRecvInventoryOnce(byte[] message, int messageLen)
        {
            TlvValueItem[] tlvItems = new TlvValueItem[6];
            byte tlvCount = 0;
            int curPos = 0;
            int paramLen = 0;
            int maxPos = 0;
            int subPos = 0;
            paramLen = message[Message_Len_Start_Pos];
            paramLen = paramLen << 8;
            paramLen = paramLen + message[Message_Len_Start_Pos + 1];
            curPos = Message_Attr_Start_Pos;
            maxPos = curPos + paramLen;

            while (curPos < maxPos)
            {
                if ((byte)Tlv_Attr_Code.TLV_ATTR_CODE_SINGLE_TAG_DATA == message[curPos])
                {
                    subPos = curPos + 2;
                    while (subPos < (curPos + 2 + message[curPos + 1]))
                    {
                        tlvItems[tlvCount] = new TlvValueItem();
                        tlvItems[tlvCount]._tlvType = message[subPos];
                        tlvItems[tlvCount]._tlvLen = message[subPos + 1];
                        tlvItems[tlvCount]._tlvValue = new byte[message[subPos + 1]];
                        Array.Copy(message, subPos + 2, tlvItems[tlvCount]._tlvValue, 0, message[subPos + 1]);
                        tlvCount++;
                        subPos = subPos + message[subPos + 1] + 2;
                    }

                }
                else if ((byte)Tlv_Attr_Code.TLV_ATTR_CODE_DEVICE_NO == message[curPos])
                {

                    tlvItems[tlvCount] = new TlvValueItem();
                    tlvItems[tlvCount]._tlvType = message[curPos];
                    tlvItems[tlvCount]._tlvLen = message[curPos + 1];
                    tlvItems[tlvCount]._tlvValue = new byte[message[curPos + 1]];
                    Array.Copy(message, curPos + 2, tlvItems[tlvCount]._tlvValue, 0, message[curPos + 1]);
                    tlvCount++;
                }
                else if ((byte)Tlv_Attr_Code.TLV_ATTR_CODE_STATUS == message[curPos])
                {
                    if (message[curPos + 2] != 0)
                    {
                        return;
                    }
                }
                curPos = curPos + message[curPos + 1] + 2;
            }
            _notifyImpl.OnRecvTagNotify(this, tlvItems, tlvCount);
        }


        public void OnRecvGetTagData(byte[] message, int messageLen)
        {
            TlvValueItem[] tlvItems = new TlvValueItem[3];
            byte tlvCount = 0;
            int curPos = 0;
            int paramLen = 0;
            int maxPos = 0;
            paramLen = message[Message_Len_Start_Pos];
            paramLen = paramLen << 8;
            paramLen = paramLen + message[Message_Len_Start_Pos + 1];
            curPos = Message_Attr_Start_Pos;
            maxPos = curPos + paramLen;

            while (curPos < maxPos)
            {
                if ((byte)Tlv_Attr_Code.TLV_ATTR_CODE_EPC == message[curPos])
                {
                    tlvItems[tlvCount] = new TlvValueItem();
                    tlvItems[tlvCount]._tlvType = message[curPos];
                    tlvItems[tlvCount]._tlvLen = message[curPos + 1];
                    tlvItems[tlvCount]._tlvValue = new byte[message[curPos + 1]];
                    Array.Copy(message, curPos + 2, tlvItems[tlvCount]._tlvValue, 0, message[curPos + 1]);
                    tlvCount++;
                }
                else if ((byte)Tlv_Attr_Code.TLV_ATTR_CODE_STATUS == message[curPos])
                {
                    if (message[curPos + 2] != 0)
                    {
                        break;
                    }
                }
                curPos = curPos + message[curPos + 1] + 2;
            }
            _notifyImpl.OnRecvTagData(this, tlvItems, tlvCount);
        }
        public override void OnRecvCompleted(byte[] messageData,int messageLen)
        {
            int statusPos = 0;
            if (null == _notifyImpl)
            {
                return;
            }           
            if (messageData[2] == 0x01)
            {
                //if the message is a response
                switch (messageData[Message_Frame_Code_Pos])
                {
                    case (byte)Frame_Code.FRAME_CODE_RESET:
                        statusPos = GetPositionByAttrType(messageData,messageLen, (byte)Tlv_Attr_Code.TLV_ATTR_CODE_STATUS);
                        if (statusPos > 0)
                        {
                            _notifyImpl.OnRecvResetRsp(this,messageData[statusPos+2]);
                        }
                        else
                        {
                            _notifyImpl.OnRecvResetRsp(this, 0xFE);
                        }
                        break;
                    case (byte)Frame_Code.FRAME_CODE_SET_DEFAULT_PARAM:
                        statusPos = GetPositionByAttrType(messageData, messageLen, (byte)Tlv_Attr_Code.TLV_ATTR_CODE_STATUS);
                        if (statusPos > 0)
                        {
                            _notifyImpl.OnRecvSetFactorySettingRsp(this, messageData[statusPos + 2]);
                        }
                        else
                        {
                            _notifyImpl.OnRecvSetFactorySettingRsp(this, 0xFE);
                        }
                        break;
                    case (byte)Frame_Code.FRAME_CODE_START_INVENTORY:
                        statusPos = GetPositionByAttrType(messageData, messageLen, (byte)Tlv_Attr_Code.TLV_ATTR_CODE_STATUS);
                        if (statusPos > 0)
                        {
                            _notifyImpl.OnRecvStartInventoryRsp(this, messageData[statusPos + 2]);
                        }
                        else
                        {
                            _notifyImpl.OnRecvStartInventoryRsp(this, 0xFE);
                        }
                        break;
                    case (byte)Frame_Code.FRAME_CODE_STOP_INVENTORY:
                        statusPos = GetPositionByAttrType(messageData, messageLen, (byte)Tlv_Attr_Code.TLV_ATTR_CODE_STATUS);
                        if (statusPos > 0)
                        {
                            _notifyImpl.OnRecvStopInventoryRsp(this, messageData[statusPos + 2]);
                        }
                        else
                        {
                            _notifyImpl.OnRecvStopInventoryRsp(this, 0xFE);
                        }
                        break;
                    case (byte)Frame_Code.FRAME_CODE_GET_DATA:
                        OnRecvGetTagData(messageData, messageLen);
                        break;
                    case (byte)Frame_Code.FRAME_CODE_INVENTORY_ONCE:
                        OnRecvInventoryOnce(messageData,messageLen);
                        break;
                    case (byte)Frame_Code.FRAME_CODE_QUERY_EXT_PARAM:
                        RfidExtParam ext_param = new RfidExtParam() ;
                        statusPos = GetPositionByAttrType(messageData, messageLen, (byte)Tlv_Attr_Code.TLV_ATTR_CODE_STATUS);
                        int ext_param_pos = GetPositionByAttrType(messageData, messageLen, (byte)Tlv_Attr_Code.TLV_ATTR_EXT_PARAM);
                        if (ext_param_pos > 0)
                        {
                            ext_param.ucRelayMode = messageData[2+ext_param_pos];
                            ext_param.ucRelayTime = messageData[3+ext_param_pos];
                            ext_param.ucVerifyFlag = messageData[4 + ext_param_pos];
                            ext_param.usVerifyPwd = messageData[5 + ext_param_pos];
                            ext_param.usVerifyPwd = (UInt16)(ext_param.usVerifyPwd << 8);
                            ext_param.usVerifyPwd += messageData[6 + ext_param_pos];
                            _notifyImpl.OnRecvQueryExtParamRsp(this, 0, ext_param);
                        }
                        else
                        {
                            _notifyImpl.OnRecvQueryExtParamRsp(this, 0x01, null);
                        }
                        break;
                    case (byte)Frame_Code.FRAME_CODE_SET_EXT_PARAM:
                        statusPos = GetPositionByAttrType(messageData, messageLen, (byte)Tlv_Attr_Code.TLV_ATTR_CODE_STATUS);
                        if (statusPos > 0)
                        {
                            _notifyImpl.OnRecvSetExtParam(this, messageData[statusPos + 2]);
                        }
                        else
                        {
                            _notifyImpl.OnRecvSetExtParam(this, 0xFE);
                        }
                        break;
                    case (byte)Frame_Code.FRAME_CODE_QUERY_DEVICE_INFO:
                        byte[] version = null;
                        statusPos = GetPositionByAttrType(messageData, messageLen, (byte)Tlv_Attr_Code.TLV_ATTR_CODE_STATUS);
                        int versionPos = GetPositionByAttrType(messageData, messageLen, (byte)Tlv_Attr_Code.TLV_ATTR_FIRMWARE_VERSION);
                        int typePos = GetPositionByAttrType(messageData, messageLen, (byte)Tlv_Attr_Code.TLV_ATTR_FIRMWARE_VERSION);
                        if (versionPos > 0)
                        {
                            version = new byte[messageData[versionPos + 1]];
                            Array.Copy(messageData, versionPos + 2, version, 0, messageData[versionPos + 1]);
                            
                        }
                        _notifyImpl.OnRecvDeviceInfoRsp(this, version, messageData[typePos + 2]);
                        break;
                    case (byte)Frame_Code.FRAME_CODE_SET_WORKING_PARAM:
                        statusPos = GetPositionByAttrType(messageData, messageLen, (byte)Tlv_Attr_Code.TLV_ATTR_CODE_STATUS);
                        if (statusPos > 0)
                        {
                            _notifyImpl.OnRecvSetWorkParamRsp(this, messageData[statusPos + 2]);
                        }
                        else
                        {
                            _notifyImpl.OnRecvSetWorkParamRsp(this, 0xFE);
                        }
                        break;
                    case (byte)Frame_Code.FRAME_CODE_QUERY_WORKING_PARAM:
                        HandleQueryWorkingParamRsp(messageData, messageLen);
                        break;
                    case (byte)Frame_Code.FRAME_CODE_SET_TRANSPORT_PARAM:
                        statusPos = GetPositionByAttrType(messageData, messageLen, (byte)Tlv_Attr_Code.TLV_ATTR_CODE_STATUS);
                        if (statusPos > 0)
                        {
                            _notifyImpl.OnRecvSetTransmissionParamRsp(this, messageData[statusPos + 2]);
                        }
                        else
                        {
                            _notifyImpl.OnRecvSetTransmissionParamRsp(this, 0xFE);
                        }
                        break;
                    case (byte)Frame_Code.FRAME_CODE_QUERY_TRANSPORT_PARAM:
                        HandleQueryTransmissionParamRsp(messageData, messageLen);
                        break;
                    case (byte)Frame_Code.FRAME_CODE_SET_ADVANCE_PARAM:
                        statusPos = GetPositionByAttrType(messageData, messageLen, (byte)Tlv_Attr_Code.TLV_ATTR_CODE_STATUS);
                        if (statusPos > 0)
                        {
                            _notifyImpl.OnRecvSetTransmissionParamRsp(this, messageData[statusPos + 2]);
                        }
                        else
                        {
                            _notifyImpl.OnRecvSetTransmissionParamRsp(this, 0xFE);
                        }
                        break;
                    case (byte)Frame_Code.FRAME_CODE_QUERY_ADVANCE_PARAM:
                        HandleQueryAdvanceParamRsp(messageData, messageLen);
                        break;
                    case (byte)Frame_Code.FRAME_CODE_SET_SIGNLE_PARAM:
                        statusPos = GetPositionByAttrType(messageData, messageLen, (byte)Tlv_Attr_Code.TLV_ATTR_CODE_STATUS);
                        if (statusPos > 0)
                        {
                            _notifyImpl.OnRecvSettingSingleParam(this, messageData[statusPos + 2]);
                        }
                        else
                        {
                            _notifyImpl.OnRecvSettingSingleParam(this, 0xFE);
                        }
                        break;
                    case (byte)Frame_Code.FRAME_CODE_QUERY_SINGLE_PARAM:
                        HandleRecvQuerySingleParam(messageData, messageLen);
                        break;
                    case (byte)Frame_Code.FRAME_CODE_WRITE_TAG:
                        statusPos = GetPositionByAttrType(messageData, messageLen, (byte)Tlv_Attr_Code.TLV_ATTR_CODE_STATUS);
                        if (statusPos > 0)
                        {
                            _notifyImpl.OnRecvWriteTagRsp(this, messageData[statusPos + 2]);
                        }
                        else
                        {
                            _notifyImpl.OnRecvWriteTagRsp(this, 0xFE);
                        }
                        break;
					case (byte)Frame_Code.FRAME_CODE_UPLOAD_RECORD_STATUS:
                        statusPos = GetPositionByAttrType(messageData, messageLen, (byte)Tlv_Attr_Code.TLV_ATTR_CODE_STATUS);
                        if (statusPos > 0)
                        {
                            _notifyImpl.OnRecvRecordStatusRsp(this, messageData[statusPos + 2]);
                        }
                        else
                        {
                            _notifyImpl.OnRecvRecordStatusRsp(this, 0xFE);
                        }
                        break;
                    case (byte)Frame_Code.FRAME_CODE_QUERY_RTC_TIME:
                        HandleRecvQueryRtcTime(messageData, messageLen);
                        break;
                    case (byte)Frame_Code.FRAME_CODE_SET_RTC_TIME:
                        statusPos = GetPositionByAttrType(messageData, messageLen, (byte)Tlv_Attr_Code.TLV_ATTR_CODE_STATUS);
                        if (statusPos > 0)
                        {
                            _notifyImpl.OnRecvSetRtcTimeStatusRsp(this, messageData[statusPos + 2]);
                        }
                        else
                        {
                            _notifyImpl.OnRecvSetRtcTimeStatusRsp(this, 0xFE);
                        }
                        break;
                    case (byte)Frame_Code.FRAME_CODE_READ_BLOCK:
                        HandleRecvReadBlockResponseByte(messageData, messageLen);
                        break;
                    case (byte)Frame_Code.FRAME_CODE_WRITE_WIEGAND_NUMBER:
                        statusPos = GetPositionByAttrType(messageData, messageLen, (byte)Tlv_Attr_Code.TLV_ATTR_CODE_STATUS);
                        _notifyImpl.OnRecvWriteWiegandNumberRsp(this, messageData[statusPos + 2]);
                        break;
                    case (byte)Frame_Code.FRAME_CODE_LOCK_TAG:
                        statusPos = GetPositionByAttrType(messageData, messageLen, (byte)Tlv_Attr_Code.TLV_ATTR_CODE_STATUS);
                        _notifyImpl.OnRecvLockResult(this, messageData[statusPos + 2]);
                        break;
                    case (byte)Frame_Code.FRAME_CODE_WRITE_EPC:
                        statusPos = GetPositionByAttrType(messageData, messageLen, (byte)Tlv_Attr_Code.TLV_ATTR_CODE_STATUS);
                        _notifyImpl.OnRecvWriteTagRsp(this, messageData[statusPos + 2]);
                        break;
                    case (byte)Frame_Code.FRAME_CODE_AUDIO_PLAY:
                        statusPos = GetPositionByAttrType(messageData, messageLen, (byte)Tlv_Attr_Code.TLV_ATTR_CODE_STATUS);
                        _notifyImpl.OnRecvAudioPlayRsp(this, messageData[statusPos + 2]);
                        break;
                    case (byte)Frame_Code.FRAME_CODE_RELAY_OP:
                        statusPos = GetPositionByAttrType(messageData, messageLen, (byte)Tlv_Attr_Code.TLV_ATTR_CODE_STATUS);
                        _notifyImpl.OnRecvRelayOpRsp(this, messageData[statusPos + 2]);
                        break;
                    case (byte)Frame_Code.FRAME_CODE_VERIFY_TAG:
                        statusPos = GetPositionByAttrType(messageData, messageLen, (byte)Tlv_Attr_Code.TLV_ATTR_CODE_STATUS);
                        _notifyImpl.OnRecvVerifyTagRsp(this, messageData[statusPos + 2]);
                        break;
                    case (byte)Frame_Code.EN_FRAME_CODE_SET_USB_DATA:
                        statusPos = GetPositionByAttrType(messageData, messageLen, (byte)Tlv_Attr_Code.TLV_ATTR_CODE_STATUS);
                        _notifyImpl.OnRecvSetUsbInfoRsp(this, messageData[statusPos + 2]);
                        break;
                    case (byte)Frame_Code.EN_FRAME_CODE_QUERY_USB_DATA:
                        HandleRecvQueryUsbInfo(messageData, messageLen);
                        break;
                    case (byte)Frame_Code.EN_FRAME_CODE_SET_DATA_FLAG:
                        statusPos = GetPositionByAttrType(messageData, messageLen, (byte)Tlv_Attr_Code.TLV_ATTR_CODE_STATUS);
                        _notifyImpl.OnRecvSetDataFlagRsp(this, messageData[statusPos + 2]);
                        break;
                    case (byte)Frame_Code.EN_FRAME_CODE_QUERY_DATA_FLAG:
                        HandleRecvQueryDataFlag(messageData, messageLen);
                        break;
                    case (byte)Frame_Code.EN_FRAME_CODE_SET_MODBUS_PARAM:
                        statusPos = GetPositionByAttrType(messageData, messageLen, (byte)Tlv_Attr_Code.TLV_ATTR_CODE_STATUS);
                        _notifyImpl.OnRecvSetModbusParamRsp(this, messageData[statusPos + 2]);
                        break;
                    case (byte)Frame_Code.EN_FRAME_CODE_QUERY_MODBUS_PARAM:
                        HandleRecvQueryModbusParam(messageData, messageLen);
                        break;
                }
            }
            else if (messageData[2] == 0x02)
            {
                //if the message is a notify.like upload tag data
                switch(messageData[5])
                {
                    case 0x80:
                        HandleRecvTagNotify(messageData, messageLen,false);
                        break;
                    case 0x81:
                        HandleRecvTagNotify(messageData, messageLen, true);
                        break;
                    case 0x82:
                        HandleRecvRecordNotify(messageData, messageLen);
                        break;
                    case 0x90:
                        HandleHearBeatsNotify(messageData, messageLen);
                        break;
                }
            }
        }
		private void HandleRecvQueryRtcTime(byte[] message, int messageLen)
        {
            int statusPos = GetPositionByAttrType(message, messageLen, (byte)Tlv_Attr_Code.TLV_ATTR_CODE_FLUSH_NOW_TIME);
            int year = 0;
            if (statusPos > 0)
            {
                year = message[statusPos + 2];
                year = year << 8;
                year = year + message[statusPos + 3];
                _notifyImpl.OnRecvQueryRtcTimeRsp(year, message[statusPos + 4], message[statusPos + 5], message[statusPos + 6],
                    message[statusPos + 7], message[statusPos + 8]);
            }
        }

        public void HandleRecvRecordNotify(byte[] message, int messageLen)
        {
            //OnRecvRecordNotify
            String time = "";
            String tagId = "";
            int tagLen = message[20];
            int year = 0;
            //time
            year = (message[10] << 8) + message[11];
            time += year.ToString()+".";
            time += message[12].ToString() + ".";
            time += message[13].ToString() + " ";
            time += message[14].ToString() + ":";
            time += message[15].ToString() + ":";
            time += message[16].ToString() + "   ";

            for (int index = 0; index < tagLen; index++)
            {
                tagId += message[21 + index].ToString("X2");
            }
            _notifyImpl.OnRecvRecordNotify(this,time,tagId);
        }
        private void HandleRecvReadBlockResponseByte(byte[] message,int messageLen)
        {
            int curPos = 0;
            int paramLen = 0;
            int maxPos = 0;
            paramLen = message[Message_Len_Start_Pos];
            paramLen = paramLen << 8;
            paramLen = paramLen + message[Message_Len_Start_Pos + 1];
            curPos = Message_Attr_Start_Pos;
            maxPos = curPos + paramLen;
            byte[] read_data = null;
            byte[] epc_data = null;
            while (curPos < maxPos)
            {
                if ((byte)Tlv_Attr_Code.TLV_ATTR_TAG_OPERATION_INFO == message[curPos])
                {
                    read_data = new byte[message[curPos + 6] * 2];
                    Array.Copy(message, curPos + 7, read_data, 0, read_data.Length);
                }
                else if ((byte)Tlv_Attr_Code.TLV_ATTR_CODE_EPC == message[curPos])
                {
                    epc_data = new byte[message[curPos + 1]];
                    Array.Copy(message, curPos + 2, epc_data, 0, epc_data.Length);
                }
                curPos = curPos + message[curPos + 1] + 2;
            }
            if (null != read_data)
            {
                _notifyImpl.OnRecvReadBlockRsp(this, 0, read_data,epc_data);
            }
            else
            {
                _notifyImpl.OnRecvReadBlockRsp(this, 1, read_data,epc_data);
            }
        }

        private void HandleRecvTagNotify(byte[] message,int messageLen,Boolean offlineFlag)
        {
            TlvValueItem[] tlvItems = new TlvValueItem[6];
            byte tlvCount = 0;
            int curPos = 0;
            int paramLen = 0;
            int maxPos = 0;
            int subPos = 0;
            paramLen = message[Message_Len_Start_Pos];
            paramLen = paramLen << 8;
            paramLen = paramLen + message[Message_Len_Start_Pos + 1];
            curPos = Message_Attr_Start_Pos;
            maxPos = curPos + paramLen;

            while (curPos < maxPos)
            {
                if ((byte)Tlv_Attr_Code.TLV_ATTR_CODE_SINGLE_TAG_DATA == message[curPos])
                {
                    subPos = curPos + 2;
                    while(subPos < (curPos+2+message[curPos+1]))
                    {
                        tlvItems[tlvCount] = new TlvValueItem();
                        tlvItems[tlvCount]._tlvType = message[subPos];
                        tlvItems[tlvCount]._tlvLen = message[subPos + 1];
                        tlvItems[tlvCount]._tlvValue = new byte[message[subPos + 1]];
                        Array.Copy(message, subPos + 2, tlvItems[tlvCount]._tlvValue, 0, message[subPos + 1]);
                        tlvCount++;
                        subPos = subPos + message[subPos + 1] +2;
                    }
                   
                }
                else if((byte)Tlv_Attr_Code.TLV_ATTR_CODE_DEVICE_NO == message[curPos])
                {

                    tlvItems[tlvCount] = new TlvValueItem();
                    tlvItems[tlvCount]._tlvType = message[curPos];
                    tlvItems[tlvCount]._tlvLen = message[curPos + 1];
                    tlvItems[tlvCount]._tlvValue = new byte[message[curPos + 1]];
                    Array.Copy(message, curPos + 2, tlvItems[tlvCount]._tlvValue, 0, message[curPos + 1]);
                    tlvCount++;
                }
                curPos = curPos + message[curPos+1] + 2;
            }
            _notifyImpl.OnRecvTagNotify(this, tlvItems,tlvCount);
        }

        private void HandleHearBeatsNotify(byte[] message, int messageLen)
        {
            _notifyImpl.OnRecvHeartBeats(this, null, 0);
        }

        private void HandleQueryWorkingParamRsp(byte[] message,int messageLen)
        {
            int statusPos = GetPositionByAttrType(message, messageLen, (byte)Tlv_Attr_Code.TLV_ATTR_CODE_STATUS);
            int workParamPos = GetPositionByAttrType(message, messageLen, (byte)Tlv_Attr_Code.TLV_ATTR_WORKING_PARAM);
            if (statusPos < 0)
            {
                _notifyImpl.OnRecvQueryWorkParamRsp(this, 0xFE, null);
                return;
            }
            else if (message[statusPos+2] != 0)
            {
                _notifyImpl.OnRecvQueryWorkParamRsp(this, message[statusPos + 2], null);
                return;
            }

            if (workParamPos < 0)
            {
                _notifyImpl.OnRecvQueryWorkParamRsp(this, 0xFE, null);
                return;
            }

            RfidWorkParam param = new RfidWorkParam();
            param.SetParamFromMessage(message, workParamPos+2);
            _notifyImpl.OnRecvQueryWorkParamRsp(this, 0, param);

        }

        private void HandleQueryTransmissionParamRsp(byte[] message, int messageLen)
        {
            int statusPos = GetPositionByAttrType(message, messageLen, (byte)Tlv_Attr_Code.TLV_ATTR_CODE_STATUS);
            int transmissionParamPos = GetPositionByAttrType(message, messageLen, (byte)Tlv_Attr_Code.TLV_ATTR_TRANSPORT_PARAM);
            if (statusPos < 0)
            {
                _notifyImpl.OnRecvQueryTransmissionParamRsp(this, 0xFE, null);
                return;
            }
            else if (message[statusPos + 2] != 0)
            {
                _notifyImpl.OnRecvQueryTransmissionParamRsp(this, message[statusPos + 2], null);
                return;
            }

            if (transmissionParamPos < 0)
            {
                _notifyImpl.OnRecvQueryTransmissionParamRsp(this, 0xFE, null);
                return;
            }

            RfidTransmissionParam param = new RfidTransmissionParam();
            param.SetParamFromMessage(message, transmissionParamPos + 2);
            _notifyImpl.OnRecvQueryTransmissionParamRsp(this, 0, param);
        }

        private void HandleQueryAdvanceParamRsp(byte[] message, int messageLen)
        {
            int statusPos = GetPositionByAttrType(message, messageLen, (byte)Tlv_Attr_Code.TLV_ATTR_CODE_STATUS);
            int advancePos = GetPositionByAttrType(message, messageLen, (byte)Tlv_Attr_Code.TLV_ATTR_ADVANCE_PARAM);
            if (statusPos < 0)
            {
                _notifyImpl.OnRecvQueryTransmissionParamRsp(this, 0xFE, null);
                return;
            }
            else if (message[statusPos + 2] != 0)
            {
                _notifyImpl.OnRecvQueryTransmissionParamRsp(this, message[statusPos + 2], null);
                return;
            }

            if (advancePos < 0)
            {
                _notifyImpl.OnRecvQueryTransmissionParamRsp(this, 0xFE, null);
                return;
            }

            RfidAdvanceParam param = new RfidAdvanceParam();
            param.SetParamFromMessage(message, advancePos + 2);
            _notifyImpl.OnRecvQueryAdvanceParamRsp(this, 0, param);
        }

        private void HandleRecvQuerySingleParam(byte[] message, int messageLen)
        {
            int statusPos = GetPositionByAttrType(message, messageLen, (byte)Tlv_Attr_Code.TLV_ATTR_CODE_STATUS);
            int paramPos = GetPositionByAttrType(message, messageLen, (byte)Tlv_Attr_Code.TLV_ATTR_SIGNLE_PARAM);
            if (statusPos < 0 || paramPos < 0)
            {
                _notifyImpl.OnRecvQuerySingleParam(this, null);
                return;
            }
            else if (message[statusPos + 2] != 0)
            {
                _notifyImpl.OnRecvQuerySingleParam(this, null);
                return;
            }
            TlvValueItem item = new TlvValueItem();
            item._tlvType = (byte)Tlv_Attr_Code.TLV_ATTR_SIGNLE_PARAM;
            item._tlvLen = message[paramPos + 1];
            item._tlvValue = new byte[item._tlvLen];
            for (int index = 0; index < item._tlvLen; index++)
            {
                item._tlvValue[index] = message[paramPos + 2 + index];
            }
            _notifyImpl.OnRecvQuerySingleParam(this, item);
        }

        private void HandleRecvQueryUsbInfo(byte[] message, int messageLen)
        {
            int curPos = 0;
            int paramLen = 0;
            int maxPos = 0;
            paramLen = message[Message_Len_Start_Pos];
            paramLen = paramLen << 8;
            paramLen = paramLen + message[Message_Len_Start_Pos + 1];
            curPos = Message_Attr_Start_Pos;
            maxPos = curPos + paramLen;

            curPos += 3;    //跳过状态
            _notifyImpl.OnRecvQueryUsbInfoRsp(this, message[curPos], message[curPos + 1], message[curPos+2]);
        }

        private void HandleRecvQueryDataFlag(byte[] message, int messageLen)
        {
            int curPos = 0;
            int paramLen = 0;
            int maxPos = 0;
            UInt16 dataflag = 0;
            byte dataFormat = 0;
            paramLen = message[Message_Len_Start_Pos];
            paramLen = paramLen << 8;
            paramLen = paramLen + message[Message_Len_Start_Pos + 1];
            curPos = Message_Attr_Start_Pos;
            maxPos = curPos + paramLen;

            curPos += 3;    //跳过状态

            dataflag = message[curPos];
            dataflag = (UInt16)(dataflag << 8);
            dataflag |= message[curPos + 1];

            dataFormat = message[curPos + 2];

            _notifyImpl.OnRecvQueryDataFlagRsp(this, dataflag,dataFormat);
        }

        private void HandleRecvQueryModbusParam(byte[] message, int messageLen)
        {
            int curPos = 0;
            int paramLen = 0;
            int maxPos = 0;
            paramLen = message[Message_Len_Start_Pos];
            paramLen = paramLen << 8;
            paramLen = paramLen + message[Message_Len_Start_Pos + 1];
            curPos = Message_Attr_Start_Pos;
            maxPos = curPos + paramLen;

            curPos += 3;    //跳过状态

            if (paramLen >= (3 + 5))
            {
                //有协议位
                if (message[curPos + 4] == 0xFF)
                {
                    _notifyImpl.OnRecvQueryModbusParam(this, message[curPos], message[curPos + 1], message[curPos + 2], message[curPos + 3], -1);
                }
                else
                {
                    _notifyImpl.OnRecvQueryModbusParam(this, message[curPos], message[curPos + 1], message[curPos + 2], message[curPos + 3], message[curPos + 4]);
                }
                
            }
            else
            {
                _notifyImpl.OnRecvQueryModbusParam(this, message[curPos], message[curPos + 1], message[curPos + 2], message[curPos + 3], -1);
            }
            
        }
        public override void HandleRecvData()
        {
            transport.HandleRecvData();
        }
    }

    enum Frame_Code
    {
        FRAME_CODE_RESET = 0x10,
        FRAME_CODE_SET_DEFAULT_PARAM = 0x12,
        FRAME_CODE_START_INVENTORY = 0x21,
        FRAME_CODE_INVENTORY_ONCE = 0x22,
        FRAME_CODE_STOP_INVENTORY = 0x23,
        FRAME_CODE_GET_DATA = 0x24,
        FRAME_CODE_WRITE_TAG = 0x30,
        FRAME_CODE_READ_BLOCK = 0x31,
        FRAME_CODE_WRITE_WIEGAND_NUMBER = 0x32,
        FRAME_CODE_LOCK_TAG = 0x33,
        FRAME_CODE_WRITE_EPC = 0x35,
        FRAME_CODE_QUERY_EXT_PARAM = 0x3E,
        FRAME_CODE_SET_EXT_PARAM = 0x3F,
        FRAME_CODE_QUERY_DEVICE_INFO = 0x40,
        FRAME_CODE_SET_WORKING_PARAM = 0x41,
        FRAME_CODE_QUERY_WORKING_PARAM = 0x42,
        FRAME_CODE_QUERY_TRANSPORT_PARAM = 0x43,
        FRAME_CODE_SET_TRANSPORT_PARAM = 0x44,
        FRAME_CODE_QUERY_ADVANCE_PARAM = 0x45,
        FRAME_CODE_SET_ADVANCE_PARAM = 0x46,
        FRAME_CODE_SET_SIGNLE_PARAM = 0x48,
        FRAME_CODE_QUERY_SINGLE_PARAM = 0x49,
        FRAME_CODE_QUERY_RTC_TIME = 0x4A,
        FRAME_CODE_SET_RTC_TIME = 0x4B,
        FRAME_CODE_RELAY_OP = 0x4C,
        FRAME_CODE_AUDIO_PLAY = 0x4D,
        FRAME_CODE_VERIFY_TAG = 0x4E,
        EN_FRAME_CODE_SET_USB_DATA = 0x50,
        EN_FRAME_CODE_QUERY_USB_DATA = 0x51,
        EN_FRAME_CODE_SET_DATA_FLAG = 0x52,
        EN_FRAME_CODE_QUERY_DATA_FLAG = 0x53,
        EN_FRAME_CODE_SET_MODBUS_PARAM = 0x54,
        EN_FRAME_CODE_QUERY_MODBUS_PARAM = 0x55,

        FRAME_CODE_UPLOAD_RECORD_STATUS = 0x72,
        FRAME_CODE_NOTIFY_TAG = 0x80,
        FRAME_CODE_PREPARE_UPDATE = 0xF4
    };

    enum Tlv_Attr_Code
    {
        TLV_ATTR_CODE_IDLE = 0x00, //
        TLV_ATTR_CODE_EPC = 0x01,    //EPC
        TLV_ATTR_CODE_USER = 0x02,   //user data in tag
        TLV_ATTR_CODE_RESERVE = 0x03,//reserve
        TLV_ATTR_CODE_TID = 0x04,    //TID data
        TLV_ATTR_CODE_RSSI = 0x05,
        TLV_ATTR_CODE_FLUSH_NOW_TIME = 0x06,
        TLV_ATTR_CODE_STATUS = 0x07,
        TLV_ATTR_TAG_OPERATION_INFO = 0x08,  //mambank addr[2] length[2]
        TLV_ATTR_ANT_NO = 0x0A,     //天线号
        TLV_ATTR_CODE_TAG_6B = 0x10,
        TLV_ATTR_FIRMWARE_VERSION = 0x20,
        TLV_ATTR_DEVICE_TYPE = 0x21,
        TLV_ATTR_WORKING_PARAM = 0x23,
        TLV_ATTR_TRANSPORT_PARAM = 0x24,
        TLV_ATTR_ADVANCE_PARAM = 0x25,
        TLV_ATTR_SIGNLE_PARAM = 0x26,
        TLV_ATTR_RELAY = 0x27,
        TLV_ATTR_AUIDO_TEXT = 0x28,
        TLV_ATTR_EXT_PARAM = 0x29,
        TLV_ATTR_CODE_SINGLE_TAG_DATA = 0x50,
        TLV_ATTR_CODE_DEVICE_NO = 0x52,
        TLV_ATTR_CODE_TEMPETURE = 0x70,
    };
}
