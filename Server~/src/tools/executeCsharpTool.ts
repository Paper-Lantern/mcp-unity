import * as z from 'zod';
import { Logger } from '../utils/logger.js';
import { McpUnity } from '../unity/mcpUnity.js';
import { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js';
import { McpUnityError, ErrorType } from '../utils/errors.js';
import { CallToolResult } from '@modelcontextprotocol/sdk/types.js';

// Constants for the tool
const toolName = 'execute_csharp';
const toolDescription = 'Executes arbitrary C# code in the Unity Editor. ' +
  'The code is compiled with Roslyn and executed on the main thread with full Unity API access. ' +
  'Write statements and end with a return for results (e.g. "return Application.dataPath;"). ' +
  'Default usings: System, System.Linq, System.Collections.Generic, System.IO, System.Text, UnityEngine, UnityEditor.';

const paramsSchema = z.object({
  code: z.string().describe(
    'C# code to compile and execute. Placed inside a static method body. ' +
    'Must end with a return statement to get results. Example: "return 2 + 2;"'
  ),
  usings: z.array(z.string()).optional().default([]).describe(
    'Additional using directives beyond the defaults. Example: ["Unity.Entities", "UnityEngine.UI"]'
  )
});

/**
 * Creates and registers the Execute C# tool with the MCP server.
 * This tool compiles and executes arbitrary C# code in the Unity Editor.
 */
export function registerExecuteCsharpTool(server: McpServer, mcpUnity: McpUnity, logger: Logger) {
  logger.info(`Registering tool: ${toolName}`);

  server.tool(
    toolName,
    toolDescription,
    paramsSchema.shape,
    async (params: any) => {
      try {
        logger.info(`Executing tool: ${toolName}`, { codeLength: params.code?.length });
        const result = await toolHandler(mcpUnity, params);
        logger.info(`Tool execution successful: ${toolName}`);
        return result;
      } catch (error) {
        logger.error(`Tool execution failed: ${toolName}`, error);
        throw error;
      }
    }
  );
}

/**
 * Handles execute_csharp tool requests
 */
async function toolHandler(mcpUnity: McpUnity, params: z.infer<typeof paramsSchema>): Promise<CallToolResult> {
  const code = params.code;
  const usings = params.usings || [];

  // Send to Unity with extended timeout (compile + execute can take time)
  const response = await mcpUnity.sendRequest({
    method: toolName,
    params: { code, usings }
  }, { timeout: 30000 });

  if (!response.success) {
    throw new McpUnityError(
      ErrorType.TOOL_EXECUTION,
      response.message || 'Failed to execute C# code'
    );
  }

  const content: Array<{ type: 'text'; text: string }> = [
    {
      type: 'text',
      text: response.message
    }
  ];

  // Include result if present
  if (response.result !== undefined && response.result !== null) {
    content.push({
      type: 'text',
      text: typeof response.result === 'string'
        ? response.result
        : JSON.stringify(response.result, null, 2)
    });
  }

  return { content };
}
