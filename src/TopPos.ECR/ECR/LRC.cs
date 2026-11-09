namespace ECR;

internal class LRC
{
	public static char calculate(string input)
	{
		return calculate(input.ToCharArray());
	}

	public static char calculate(char[] input)
	{
		char c = input[0];
		for (int i = 0; i < input.Length; i++)
		{
			c ^= input[i];
		}
		return c;
	}
}
