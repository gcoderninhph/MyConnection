namespace MyConnection;

public class ApiException : Exception
{
    public string ErrorCode { get; }

    public ApiException(string errorCode, string errorMessage) : base(errorMessage)
    {
        ErrorCode = errorCode;
    }
}
