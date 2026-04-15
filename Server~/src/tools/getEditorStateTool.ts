import { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js';
import { McpUnity } from '../unity/mcpUnity.js';
import { McpUnityError, ErrorType } from '../utils/errors.js';
import * as z from 'zod';
import { Logger } from '../utils/logger.js';

const toolName = 'get_editor_state';
const toolDescription =
  'Returns Unity Editor state flags (isPlaying, isPlayingOrWillChangePlaymode, isCompiling, ' +
  'isUpdating, isPaused). Side-effect free — safe to call anytime without triggering Asset ' +
  'Pipeline Refresh or assembly reload. Use this before execute_csharp / recompile_scripts / ' +
  'any file-mutating call to avoid Play Mode freeze.';

const paramsSchema = z.object({});

export function registerGetEditorStateTool(server: McpServer, mcpUnity: McpUnity, logger: Logger) {
  logger.info(`Registering tool: ${toolName}`);

  server.tool(
    toolName,
    toolDescription,
    paramsSchema.shape,
    async (params: any) => {
      try {
        logger.info(`Executing tool: ${toolName}`);
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

async function toolHandler(mcpUnity: McpUnity, params: any) {
  const response = await mcpUnity.sendRequest({
    method: toolName,
    params
  });

  if (!response.success) {
    throw new McpUnityError(
      ErrorType.TOOL_EXECUTION,
      response.message || 'Failed to fetch editor state'
    );
  }

  const flags = {
    isPlaying: !!response.isPlaying,
    isPlayingOrWillChangePlaymode: !!response.isPlayingOrWillChangePlaymode,
    isCompiling: !!response.isCompiling,
    isUpdating: !!response.isUpdating,
    isPaused: !!response.isPaused
  };

  const summary = Object.entries(flags)
    .map(([k, v]) => `${k}=${v}`)
    .join(', ');

  return {
    content: [{
      type: 'text' as const,
      text: `Editor state: ${summary}`
    }],
    data: flags
  };
}
