using System.Text.Json.Serialization;

namespace FuzhouPolicyForce.Models
{
    public class ShenTongLanShouRequest
    {
        [JsonPropertyName("pdaCode")] public string? PdaCode { get; set; }
        [JsonPropertyName("opTerminal")] public string? OpTerminal { get; set; }
        [JsonPropertyName("clientProgramRole")] public string? ClientProgramRole { get; set; }
        [JsonPropertyName("deviceType")] public string? DeviceType { get; set; }
        [JsonPropertyName("orgCode")] public string? OrgCode { get; set; }
        [JsonPropertyName("userCode")] public string? UserCode { get; set; }
        [JsonPropertyName("opSaAccountId")] public string? OpSaAccountId { get; set; }
        [JsonPropertyName("buildingCode")] public string? BuildingCode { get; set; }
        [JsonPropertyName("buildingName")] public string? BuildingName { get; set; }
        [JsonPropertyName("storehouseCode")] public string? StorehouseCode { get; set; }
        [JsonPropertyName("storehouseName")] public string? StorehouseName { get; set; }
        [JsonPropertyName("receiveSource")] public string? ReceiveSource { get; set; }
        [JsonPropertyName("records")] public List<ShenTongLanShouRecordDto>? Records { get; set; }
    }

    public class ShenTongLanShouRecordDto
    {
        [JsonPropertyName("uuid")] public string? Uuid { get; set; }
        [JsonPropertyName("waybillNo")] public string? WaybillNo { get; set; }
        [JsonPropertyName("expType")] public string? ExpType { get; set; }
        [JsonPropertyName("opCode")] public string? OpCode { get; set; }
        [JsonPropertyName("goodsType")] public string? GoodsType { get; set; }
        [JsonPropertyName("effectiveType")] public string? EffectiveType { get; set; }
        [JsonPropertyName("desAreaCode")] public string? DesAreaCode { get; set; }
        [JsonPropertyName("customerde")] public string? Customerde { get; set; }
        [JsonPropertyName("sendMobilePhone")] public string? SendMobilePhone { get; set; }
        [JsonPropertyName("receiveMobilePhone")] public string? ReceiveMobilePhone { get; set; }
        [JsonPropertyName("opTime")] public string? OpTime { get; set; }
        [JsonPropertyName("customcode")] public string? Customcode { get; set; }
        [JsonPropertyName("refId")] public string? RefId { get; set; }
        [JsonPropertyName("weight")] public string? Weight { get; set; }
        [JsonPropertyName("inputWeight")] public string? InputWeight { get; set; }
        [JsonPropertyName("empCode")] public string? EmpCode { get; set; }
        [JsonPropertyName("frequencyNo")] public string? FrequencyNo { get; set; }
        [JsonPropertyName("nextOrgCode")] public string? NextOrgCode { get; set; }
        [JsonPropertyName("containerNo")] public string? ContainerNo { get; set; }
        [JsonPropertyName("lineNo")] public string? LineNo { get; set; }
        [JsonPropertyName("routeCode")] public string? RouteCode { get; set; }
        [JsonPropertyName("lastOrgCode")] public string? LastOrgCode { get; set; }
        [JsonPropertyName("length")] public string? Length { get; set; }
        [JsonPropertyName("width")] public string? Width { get; set; }
        [JsonPropertyName("height")] public string? Height { get; set; }
        [JsonPropertyName("volume")] public string? Volume { get; set; }
        [JsonPropertyName("vehicleId")] public string? VehicleId { get; set; }
        [JsonPropertyName("recieverSignoff")] public string? RecieverSignoff { get; set; }
        [JsonPropertyName("signoffImg")] public string? SignoffImg { get; set; }
        [JsonPropertyName("issueType")] public string? IssueType { get; set; }
        [JsonPropertyName("issueImg")] public string? IssueImg { get; set; }
        [JsonPropertyName("expressCabinet")] public string? ExpressCabinet { get; set; }
        [JsonPropertyName("storeCode")] public string? StoreCode { get; set; }
        [JsonPropertyName("transportTaskNo")] public string? TransportTaskNo { get; set; }
        [JsonPropertyName("carNo")] public string? CarNo { get; set; }
        [JsonPropertyName("leadSealingNo")] public string? LeadSealingNo { get; set; }
        [JsonPropertyName("leadSealingNumber")] public string? LeadSealingNumber { get; set; }
        [JsonPropertyName("voiceUrl")] public string? VoiceUrl { get; set; }
        [JsonPropertyName("longitudeAndLatitude")] public string? LongitudeAndLatitude { get; set; }
        [JsonPropertyName("agentStationType")] public string? AgentStationType { get; set; }
        [JsonPropertyName("agentStationCode")] public string? AgentStationCode { get; set; }
        [JsonPropertyName("agentStationBizCode")] public string? AgentStationBizCode { get; set; }
        [JsonPropertyName("agentStationDeliveryFlag")] public string? AgentStationDeliveryFlag { get; set; }
        [JsonPropertyName("imgUrl")] public string? ImgUrl { get; set; }
        [JsonPropertyName("opFlag")] public int? OpFlag { get; set; }
        [JsonPropertyName("opUnifyFlag")] public string? OpUnifyFlag { get; set; }
        [JsonPropertyName("storageId")] public string? StorageId { get; set; }
        [JsonPropertyName("storageOutType")] public string? StorageOutType { get; set; }
        [JsonPropertyName("refundType")] public string? RefundType { get; set; }
        [JsonPropertyName("refundDesc")] public string? RefundDesc { get; set; }
        [JsonPropertyName("remark")] public string? Remark { get; set; }
        [JsonPropertyName("bizEmpCode")] public string? BizEmpCode { get; set; }
        [JsonPropertyName("issueTypeExtend")] public string? IssueTypeExtend { get; set; }
        [JsonPropertyName("issueImgExtend")] public string? IssueImgExtend { get; set; }
        [JsonPropertyName("issueDesc")] public string? IssueDesc { get; set; }
        [JsonPropertyName("sendOnBrandName")] public string? SendOnBrandName { get; set; }
        [JsonPropertyName("sendOnBrandCode")] public string? SendOnBrandCode { get; set; }
        [JsonPropertyName("sendOnWaybillNo")] public string? SendOnWaybillNo { get; set; }
        [JsonPropertyName("replaceOp")] public string? ReplaceOp { get; set; }
        [JsonPropertyName("platformName")] public string? PlatformName { get; set; }
        [JsonPropertyName("areaCode")] public string? AreaCode { get; set; }
        [JsonPropertyName("areaName")] public string? AreaName { get; set; }
        [JsonPropertyName("dispatchNo")] public string? DispatchNo { get; set; }
        [JsonPropertyName("signInType")] public string? SignInType { get; set; }
        [JsonPropertyName("businessCode")] public string? BusinessCode { get; set; }
        [JsonPropertyName("businessAddress")] public string? BusinessAddress { get; set; }
        [JsonPropertyName("replaceDeliveryCode")] public string? ReplaceDeliveryCode { get; set; }
        [JsonPropertyName("loopBagNo")] public string? LoopBagNo { get; set; }
        [JsonPropertyName("chuteNo")] public string? ChuteNo { get; set; }
        [JsonPropertyName("deviceId")] public string? DeviceId { get; set; }
        [JsonPropertyName("issueBelongsArea")] public string? IssueBelongsArea { get; set; }
        [JsonPropertyName("issueDamageDegree")] public string? IssueDamageDegree { get; set; }
        [JsonPropertyName("transportType")] public string? TransportType { get; set; }
        [JsonPropertyName("failFlag")] public string? FailFlag { get; set; }
        [JsonPropertyName("excFlag")] public string? ExcFlag { get; set; }
        [JsonPropertyName("signType")] public string? SignType { get; set; }
        [JsonPropertyName("signTypeDesc")] public string? SignTypeDesc { get; set; }
        [JsonPropertyName("transportTaskName")] public string? TransportTaskName { get; set; }
        [JsonPropertyName("forwardingCompany")] public string? ForwardingCompany { get; set; }
        [JsonPropertyName("chipNumber")] public string? ChipNumber { get; set; }
        [JsonPropertyName("virtualOrgCode")] public string? VirtualOrgCode { get; set; }
        [JsonPropertyName("deviceName")] public string? DeviceName { get; set; }
        [JsonPropertyName("productKey")] public string? ProductKey { get; set; }
        [JsonPropertyName("source")] public string? Source { get; set; }
        [JsonPropertyName("osVersion")] public string? OsVersion { get; set; }
        [JsonPropertyName("deviceTypeNew")] public string? DeviceTypeNew { get; set; }
        [JsonPropertyName("manufacturer")] public string? Manufacturer { get; set; }
        [JsonPropertyName("model")] public string? Model { get; set; }
        [JsonPropertyName("sn")] public string? Sn { get; set; }
        [JsonPropertyName("ip")] public string? Ip { get; set; }
        [JsonPropertyName("softVersion")] public string? SoftVersion { get; set; }
    }
} 