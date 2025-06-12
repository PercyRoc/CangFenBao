using System.Collections.Generic;

namespace XinBa.Services.Models
{
    public class ErrorDetail
    {
        public string error { get; set; }
        public string message { get; set; }
        public string detail { get; set; }
    }

    public class ErrorResponse
    {
        public List<ErrorDetail> errors { get; set; }
    }
} 