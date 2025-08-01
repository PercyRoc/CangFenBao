using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace RfidSdk
{
    public interface RfidReaderRspNotify
    {
        void OnRecvResetRsp(RfidReader reader,byte result);
        void OnRecvSetFactorySettingRsp(RfidReader reader, byte result);
        void OnRecvStartInventoryRsp(RfidReader reader, byte result);
        void OnRecvStopInventoryRsp(RfidReader reader, byte result);
        void OnRecvDeviceInfoRsp(RfidReader reader, byte[] firmwareVersion,byte deviceType);
        void OnRecvSetWorkParamRsp(RfidReader reader, byte result);

        void OnRecvQueryWorkParamRsp(RfidReader reader, byte result, RfidWorkParam workParam);

        void OnRecvSetTransmissionParamRsp(RfidReader reader, byte result);

        void OnRecvQueryTransmissionParamRsp(RfidReader reader, byte result, RfidTransmissionParam transmissiomParam);

        void OnRecvSetAdvanceParamRsp(RfidReader reader, byte result);

        void OnRecvQueryAdvanceParamRsp(RfidReader reader, byte result, RfidAdvanceParam advanceParam);

        void OnRecvTagNotify(RfidReader reader, TlvValueItem[] tlvItems, byte tlvCount);

        void OnRecvHeartBeats(RfidReader reader, TlvValueItem[] tlvItems, byte tlvCount);

        void OnRecvSettingSingleParam(RfidReader reader, byte result);

        void OnRecvQuerySingleParam(RfidReader reader, TlvValueItem item);

        void OnRecvWriteTagRsp(RfidReader reader, byte result);

		void OnRecvRecordNotify(RfidReader reader, String time, String tagId);

        void OnRecvRecordStatusRsp(RfidReader reader, byte result);

        void OnRecvSetRtcTimeStatusRsp(RfidReader reader, byte result);

        void OnRecvQueryRtcTimeRsp(int year, int month, int day, int hour, int min, int sec);
		
        void OnRecvReadBlockRsp(RfidReader reader, byte result, byte[] read_data,byte[] epc_data);
		
        void OnRecvWriteWiegandNumberRsp(RfidReader reader, byte result);

        void OnRecvLockResult(RfidReader reader, byte result);

        void OnRecvQueryExtParamRsp(RfidReader reader, byte result,RfidExtParam extParam);

        void OnRecvSetExtParam(RfidReader reader, byte result);

        void OnRecvAudioPlayRsp(RfidSdk.RfidReader reader, byte result);

        void OnRecvRelayOpRsp(RfidSdk.RfidReader reader, byte result);

        void OnRecvVerifyTagRsp(RfidSdk.RfidReader reader, byte result);

        void OnRecvSetUsbInfoRsp(RfidSdk.RfidReader reader, byte result);

        void OnRecvQueryUsbInfoRsp(RfidSdk.RfidReader reader,byte interfaceType,byte usbProto, byte enterflag);

        void OnRecvSetDataFlagRsp(RfidSdk.RfidReader reader, byte result);

        void OnRecvQueryDataFlagRsp(RfidSdk.RfidReader reader, UInt16 dataflag,byte dataformat);

        void OnRecvQueryModbusParam(RfidSdk.RfidReader reader, byte tagNum, byte unionSize, byte startaddr, byte clearflag,int modbusProto);

        void OnRecvSetModbusParamRsp(RfidSdk.RfidReader reader, byte result);

        void OnRecvTagData(RfidReader reader, TlvValueItem[] tlvItems, byte tlvCount);
    }
}
