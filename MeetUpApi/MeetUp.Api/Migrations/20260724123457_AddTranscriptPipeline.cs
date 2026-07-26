using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeetUp.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddTranscriptPipeline : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Transcripts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MeetingId = table.Column<Guid>(type: "uuid", nullable: false),
                    Language = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    FullText = table.Column<string>(type: "text", nullable: false),
                    AssemblyAiTranscriptId = table.Column<string>(type: "text", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Transcripts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Transcripts_Meetings_MeetingId",
                        column: x => x.MeetingId,
                        principalTable: "Meetings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TranscriptUtterances",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TranscriptId = table.Column<Guid>(type: "uuid", nullable: false),
                    SpeakerLabel = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    ParticipantUserId = table.Column<string>(type: "text", nullable: true),
                    StartMs = table.Column<int>(type: "integer", nullable: false),
                    EndMs = table.Column<int>(type: "integer", nullable: false),
                    Text = table.Column<string>(type: "text", nullable: false),
                    Confidence = table.Column<float>(type: "real", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TranscriptUtterances", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TranscriptUtterances_AspNetUsers_ParticipantUserId",
                        column: x => x.ParticipantUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_TranscriptUtterances_Transcripts_TranscriptId",
                        column: x => x.TranscriptId,
                        principalTable: "Transcripts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Transcripts_MeetingId",
                table: "Transcripts",
                column: "MeetingId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TranscriptUtterances_ParticipantUserId",
                table: "TranscriptUtterances",
                column: "ParticipantUserId");

            migrationBuilder.CreateIndex(
                name: "IX_TranscriptUtterances_TranscriptId_StartMs",
                table: "TranscriptUtterances",
                columns: new[] { "TranscriptId", "StartMs" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TranscriptUtterances");

            migrationBuilder.DropTable(
                name: "Transcripts");
        }
    }
}
