from __future__ import annotations
import datetime
from collections.abc import Callable
from dataclasses import dataclass, field
from kiota_abstractions.serialization import AdditionalDataHolder, Parsable, ParseNode, SerializationWriter
from typing import Any, Optional, TYPE_CHECKING, Union
from uuid import UUID

@dataclass
class ExportResponse(AdditionalDataHolder, Parsable):
    # Stores additional data not described in the OpenAPI description found when deserializing. Can be used for serialization as well.
    additional_data: dict[str, Any] = field(default_factory=dict)

    # The createdAt property
    created_at: Optional[datetime.datetime] = None
    # The expiresAt property
    expires_at: Optional[datetime.datetime] = None
    # The id property
    id: Optional[UUID] = None
    # The operationId property
    operation_id: Optional[UUID] = None
    # The packageUrl property
    package_url: Optional[str] = None
    # The ready property
    ready: Optional[bool] = None
    # The size property
    size: Optional[int] = None
    # The workspaceId property
    workspace_id: Optional[UUID] = None
    
    @staticmethod
    def create_from_discriminator_value(parse_node: ParseNode) -> ExportResponse:
        """
        Creates a new instance of the appropriate class based on discriminator value
        param parse_node: The parse node to use to read the discriminator value and create the object
        Returns: ExportResponse
        """
        if parse_node is None:
            raise TypeError("parse_node cannot be null.")
        return ExportResponse()
    
    def get_field_deserializers(self,) -> dict[str, Callable[[ParseNode], None]]:
        """
        The deserialization information for the current model
        Returns: dict[str, Callable[[ParseNode], None]]
        """
        fields: dict[str, Callable[[Any], None]] = {
            "createdAt": lambda n : setattr(self, 'created_at', n.get_datetime_value()),
            "expiresAt": lambda n : setattr(self, 'expires_at', n.get_datetime_value()),
            "id": lambda n : setattr(self, 'id', n.get_uuid_value()),
            "operationId": lambda n : setattr(self, 'operation_id', n.get_uuid_value()),
            "packageUrl": lambda n : setattr(self, 'package_url', n.get_str_value()),
            "ready": lambda n : setattr(self, 'ready', n.get_bool_value()),
            "size": lambda n : setattr(self, 'size', n.get_int_value()),
            "workspaceId": lambda n : setattr(self, 'workspace_id', n.get_uuid_value()),
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
        writer.write_datetime_value("createdAt", self.created_at)
        writer.write_datetime_value("expiresAt", self.expires_at)
        writer.write_uuid_value("id", self.id)
        writer.write_uuid_value("operationId", self.operation_id)
        writer.write_str_value("packageUrl", self.package_url)
        writer.write_bool_value("ready", self.ready)
        writer.write_int_value("size", self.size)
        writer.write_uuid_value("workspaceId", self.workspace_id)
        writer.write_additional_data_value(self.additional_data)
    

