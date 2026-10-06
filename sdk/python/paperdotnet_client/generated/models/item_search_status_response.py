from __future__ import annotations
import datetime
from collections.abc import Callable
from dataclasses import dataclass, field
from kiota_abstractions.serialization import AdditionalDataHolder, Parsable, ParseNode, SerializationWriter
from typing import Any, Optional, TYPE_CHECKING, Union

if TYPE_CHECKING:
    from .workflow_run_info import WorkflowRunInfo

@dataclass
class ItemSearchStatusResponse(AdditionalDataHolder, Parsable):
    # Stores additional data not described in the OpenAPI description found when deserializing. Can be used for serialization as well.
    additional_data: dict[str, Any] = field(default_factory=dict)

    # The chunks property
    chunks: Optional[int] = None
    # The contentState property
    content_state: Optional[str] = None
    # The currentRevision property
    current_revision: Optional[str] = None
    # The embeddingModel property
    embedding_model: Optional[str] = None
    # The embeddingState property
    embedding_state: Optional[str] = None
    # The included property
    included: Optional[bool] = None
    # The indexed property
    indexed: Optional[bool] = None
    # The indexedAt property
    indexed_at: Optional[datetime.datetime] = None
    # The indexedRevision property
    indexed_revision: Optional[str] = None
    # The run property
    run: Optional[WorkflowRunInfo] = None
    # The state property
    state: Optional[str] = None
    # The truncated property
    truncated: Optional[bool] = None
    
    @staticmethod
    def create_from_discriminator_value(parse_node: ParseNode) -> ItemSearchStatusResponse:
        """
        Creates a new instance of the appropriate class based on discriminator value
        param parse_node: The parse node to use to read the discriminator value and create the object
        Returns: ItemSearchStatusResponse
        """
        if parse_node is None:
            raise TypeError("parse_node cannot be null.")
        return ItemSearchStatusResponse()
    
    def get_field_deserializers(self,) -> dict[str, Callable[[ParseNode], None]]:
        """
        The deserialization information for the current model
        Returns: dict[str, Callable[[ParseNode], None]]
        """
        from .workflow_run_info import WorkflowRunInfo

        from .workflow_run_info import WorkflowRunInfo

        fields: dict[str, Callable[[Any], None]] = {
            "chunks": lambda n : setattr(self, 'chunks', n.get_int_value()),
            "contentState": lambda n : setattr(self, 'content_state', n.get_str_value()),
            "currentRevision": lambda n : setattr(self, 'current_revision', n.get_str_value()),
            "embeddingModel": lambda n : setattr(self, 'embedding_model', n.get_str_value()),
            "embeddingState": lambda n : setattr(self, 'embedding_state', n.get_str_value()),
            "included": lambda n : setattr(self, 'included', n.get_bool_value()),
            "indexed": lambda n : setattr(self, 'indexed', n.get_bool_value()),
            "indexedAt": lambda n : setattr(self, 'indexed_at', n.get_datetime_value()),
            "indexedRevision": lambda n : setattr(self, 'indexed_revision', n.get_str_value()),
            "run": lambda n : setattr(self, 'run', n.get_object_value(WorkflowRunInfo)),
            "state": lambda n : setattr(self, 'state', n.get_str_value()),
            "truncated": lambda n : setattr(self, 'truncated', n.get_bool_value()),
        }
        return fields
    
    def serialize(self,writer: SerializationWriter) -> None:
        """
        Serializes information the current object
        param writer: Serialization writer to use to serialize this model
        Returns: None
        """
        if writer is None:
            raise TypeError("writer cannot be null.")
        writer.write_int_value("chunks", self.chunks)
        writer.write_str_value("contentState", self.content_state)
        writer.write_str_value("currentRevision", self.current_revision)
        writer.write_str_value("embeddingModel", self.embedding_model)
        writer.write_str_value("embeddingState", self.embedding_state)
        writer.write_bool_value("included", self.included)
        writer.write_bool_value("indexed", self.indexed)
        writer.write_datetime_value("indexedAt", self.indexed_at)
        writer.write_str_value("indexedRevision", self.indexed_revision)
        writer.write_object_value("run", self.run)
        writer.write_str_value("state", self.state)
        writer.write_bool_value("truncated", self.truncated)
        writer.write_additional_data_value(self.additional_data)
    

