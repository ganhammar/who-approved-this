using Amazon.CDK;

namespace WhoApprovedThis.Infrastructure;

sealed class Program
{
    public static void Main()
    {
        var app = new App();
        _ = new WhoApprovedThisStack(app, "WhoApprovedThis", new StackProps
        {
            Env = new Amazon.CDK.Environment
            {
                Account = System.Environment.GetEnvironmentVariable("CDK_DEFAULT_ACCOUNT"),
                Region = "eu-north-1",
            },
        });
        app.Synth();
    }
}
