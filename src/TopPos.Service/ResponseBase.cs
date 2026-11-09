namespace TopPos.Service
{
	public class ResponseBase
	{
		public ResponseStatus ResponseStatus { get; set; } = default!;
	}

	public class ResponseStatus 
	{
		public string ErrorCode { get; set; } = default!;
		public string Message { get; set; } = default!;
		public string StackTrace { get; set; } = default!;
		public List<ResponseError> Errors { get; set; } = default!;
		public Dictionary<string, string> Meta { get; set; } = default!;
	}

	public class ResponseError
	{
		public string ErrorCode { get; set; } = default!;

		public string FieldName { get; set; } = default!;

		public string Message { get; set; } = default!;

		public Dictionary<string, string> Meta { get; set; } = default!;
	}
}
