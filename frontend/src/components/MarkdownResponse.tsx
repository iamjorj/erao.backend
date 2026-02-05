"use client";

import React from "react";

interface MarkdownResponseProps {
  content: string;
}

export function MarkdownResponse({ content }: MarkdownResponseProps) {
  const parseContent = (text: string) => {
    const lines = text.split("\n");
    const elements: React.ReactNode[] = [];
    let currentList: string[] = [];
    let listKey = 0;

    const flushList = () => {
      if (currentList.length > 0) {
        elements.push(
          <ul key={`list-${listKey++}`} className="space-y-1.5 my-3 ml-4">
            {currentList.map((item, idx) => (
              <li key={idx} className="flex gap-2.5 text-sm">
                <span className="text-gray-400 dark:text-gray-500 mt-2 flex-shrink-0">
                  <span className="block w-1 h-1 bg-current rounded-full"></span>
                </span>
                <span className="text-gray-600 dark:text-gray-400 leading-relaxed">{renderInlineStyles(item)}</span>
              </li>
            ))}
          </ul>
        );
        currentList = [];
      }
    };

    // Render inline styles (bold, code, stats)
    const renderInlineStyles = (text: string): React.ReactNode => {
      // First handle code blocks with backticks
      const processedParts: React.ReactNode[] = [];
      const codeSplit = text.split(/(`[^`]+`)/g);

      codeSplit.forEach((segment, segIdx) => {
        if (segment.startsWith("`") && segment.endsWith("`")) {
          // Inline code
          processedParts.push(
            <code key={`code-${segIdx}`} className="px-1.5 py-0.5 bg-gray-100 dark:bg-gray-800 text-gray-800 dark:text-gray-200 rounded text-xs font-mono">
              {segment.slice(1, -1)}
            </code>
          );
        } else {
          // Process bold and stats
          const boldParts = segment.split(/(\*\*[^*]+\*\*)/g);
          boldParts.forEach((part, partIdx) => {
            if (part.startsWith("**") && part.endsWith("**")) {
              const boldText = part.slice(2, -2);
              processedParts.push(
                <span key={`bold-${segIdx}-${partIdx}`} className="font-semibold text-gray-900 dark:text-white">
                  {boldText}
                </span>
              );
            } else {
              // Highlight statistics (percentages, money, specific patterns)
              const statPattern = /(\d+\.?\d*%|\$[\d,]+\.?\d*[KMB]?|\d+\.?\d*\s*(?:hours?|minutes?|days?|out of \d+|\/\d+)|\bCGPA\s*\d+\.?\d*|\d+\.?\d*\s*CGPA)/gi;
              const statParts = part.split(statPattern);

              statParts.forEach((statPart, statIdx) => {
                if (statPattern.test(statPart)) {
                  // Reset regex lastIndex
                  statPattern.lastIndex = 0;
                  processedParts.push(
                    <span key={`stat-${segIdx}-${partIdx}-${statIdx}`} className="font-semibold text-gray-900 dark:text-white">
                      {statPart}
                    </span>
                  );
                } else {
                  processedParts.push(statPart);
                }
              });
            }
          });
        }
      });

      return processedParts;
    };

    lines.forEach((line, idx) => {
      const trimmedLine = line.trim();

      if (!trimmedLine) {
        flushList();
        return;
      }

      // Bullet point
      if (trimmedLine.startsWith("- ") || trimmedLine.startsWith("• ")) {
        currentList.push(trimmedLine.slice(2));
        return;
      }

      // Numbered list
      const numberedMatch = trimmedLine.match(/^\d+\.\s+(.+)/);
      if (numberedMatch) {
        currentList.push(numberedMatch[1]);
        return;
      }

      flushList();

      // Main section header (starts and ends with **)
      if (trimmedLine.startsWith("**") && trimmedLine.endsWith("**")) {
        const headerText = trimmedLine.slice(2, -2).replace(/:$/, "");
        elements.push(
          <div key={idx} className="mt-4 mb-1.5 first:mt-0">
            <h3 className="text-xs font-medium text-gray-500 dark:text-gray-400 uppercase tracking-wider">
              {headerText}
            </h3>
          </div>
        );
        return;
      }

      // Header with content (**Header:** content)
      const headerWithContent = trimmedLine.match(/^\*\*([^*]+):\*\*\s*(.*)$/);
      if (headerWithContent) {
        elements.push(
          <div key={idx} className="mt-4 mb-1.5 first:mt-0">
            <h3 className="text-xs font-medium text-gray-500 dark:text-gray-400 uppercase tracking-wider">
              {headerWithContent[1]}
            </h3>
            {headerWithContent[2] && (
              <p className="text-sm text-gray-600 dark:text-gray-400 mt-1.5 leading-relaxed">
                {renderInlineStyles(headerWithContent[2])}
              </p>
            )}
          </div>
        );
        return;
      }

      // Horizontal rule
      if (trimmedLine === "---" || trimmedLine === "***") {
        elements.push(
          <div key={idx} className="h-px bg-gray-200 dark:bg-gray-700 my-4"></div>
        );
        return;
      }

      // Regular paragraph
      elements.push(
        <p key={idx} className="text-sm text-gray-600 dark:text-gray-400 leading-relaxed my-1.5">
          {renderInlineStyles(trimmedLine)}
        </p>
      );
    });

    flushList();
    return elements;
  };

  return (
    <div className="space-y-0.5">
      {parseContent(content)}
    </div>
  );
}
