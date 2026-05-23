System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
var app = new FrxEditApp(Console.Out, Console.Error);
return app.Run(args);
