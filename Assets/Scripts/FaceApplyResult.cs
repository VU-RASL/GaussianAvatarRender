using System;

[Serializable]
public struct FaceApplyResult
{
    public bool success;
    public bool jawApplied;
    public bool expressionApplied;
    public bool eyelidApplied;
    public bool exactExpression50;
    public string message;

    public FaceApplyResult(
        bool success,
        bool jawApplied,
        bool expressionApplied,
        bool eyelidApplied,
        bool exactExpression50,
        string message)
    {
        this.success = success;
        this.jawApplied = jawApplied;
        this.expressionApplied = expressionApplied;
        this.eyelidApplied = eyelidApplied;
        this.exactExpression50 = exactExpression50;
        this.message = message;
    }

    public static FaceApplyResult Failed(string message)
    {
        return new FaceApplyResult(false, false, false, false, false, message);
    }

    public override string ToString()
    {
        return message;
    }
}
